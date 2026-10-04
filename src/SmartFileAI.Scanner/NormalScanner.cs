using System.IO;
using System.Runtime.CompilerServices;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models;
using SmartFileAI.Core.Models.Entities;

namespace SmartFileAI.Scanner;

public sealed class NormalScanner : IFileScanner
{
    private const int ProgressReportIntervalMs = 150;

    public async IAsyncEnumerable<FileItem> ScanDirectoryAsync(
        string rootPath,
        IProgress<ScanProgressReport>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Scan root path cannot be empty.", nameof(rootPath));

        string normalizedRoot = NormalizeDirectoryPath(rootPath);

        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException(normalizedRoot);

        var stack = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        stack.Push(normalizedRoot);

        long scannedFiles = 0;
        long scannedDirs = 0;
        long totalSize = 0;
        DateTime lastReport = DateTime.UtcNow;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string currentDir = stack.Pop();
            DirectoryInfo dirInfo;

            try
            {
                dirInfo = new DirectoryInfo(currentDir);
                if (!visited.Add(NormalizeDirectoryPath(dirInfo.FullName)))
                    continue;

                scannedDirs++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException or DirectoryNotFoundException)
            {
                ReportWarning(progress, currentDir, scannedFiles, scannedDirs, totalSize, ex.Message);
                continue;
            }

            IEnumerable<DirectoryInfo> dirs;

            try
            {
                dirs = dirInfo.EnumerateDirectories();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException or DirectoryNotFoundException)
            {
                ReportWarning(progress, currentDir, scannedFiles, scannedDirs, totalSize, ex.Message);
                dirs = Array.Empty<DirectoryInfo>();
            }

            foreach (var subDir in dirs)
            {
                var attributes = subDir.Attributes;

                var item = new FileItem
                {
                    Name = subDir.Name,
                    FullPath = NormalizePath(subDir.FullName),
                    ParentPath = NormalizeDirectoryPath(currentDir),
                    IsDirectory = true,
                    Size = 0,
                    Extension = string.Empty,
                    CreatedTime = subDir.CreationTime,
                    ModifiedTime = subDir.LastWriteTime,
                    Attributes = attributes
                };

                cancellationToken.ThrowIfCancellationRequested();

                yield return item;

                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    stack.Push(item.FullPath);
            }

            IEnumerable<FileInfo> files;

            try
            {
                files = dirInfo.EnumerateFiles();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException or DirectoryNotFoundException)
            {
                ReportWarning(progress, currentDir, scannedFiles, scannedDirs, totalSize, ex.Message);
                files = Array.Empty<FileInfo>();
            }

            foreach (var fileInfo in files)
            {
                FileItem item;

                try
                {
                    long size = fileInfo.Length;

                    item = new FileItem
                    {
                        Name = fileInfo.Name,
                        FullPath = NormalizePath(fileInfo.FullName),
                        ParentPath = NormalizeDirectoryPath(currentDir),
                        IsDirectory = false,
                        Size = size,
                        Extension = fileInfo.Extension.ToLowerInvariant(),
                        CreatedTime = fileInfo.CreationTime,
                        ModifiedTime = fileInfo.LastWriteTime,
                        Attributes = fileInfo.Attributes
                    };

                    scannedFiles++;
                    totalSize += size;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
                {
                    ReportWarning(progress, fileInfo.FullName, scannedFiles, scannedDirs, totalSize, ex.Message);
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                yield return item;
            }

            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > ProgressReportIntervalMs)
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new ScanProgressReport(currentDir, scannedFiles, scannedDirs, totalSize));
                await Task.Yield();
            }
        }

        progress?.Report(new ScanProgressReport(normalizedRoot, scannedFiles, scannedDirs, totalSize));
    }

    private static void ReportWarning(IProgress<ScanProgressReport>? progress, string path, long files, long dirs, long bytes, string warning)
        => progress?.Report(new ScanProgressReport(path, files, dirs, bytes, warning));

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string NormalizeDirectoryPath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
