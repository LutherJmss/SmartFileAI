using System;
using System.Collections.Generic;
using System.IO;

namespace SmartFileAI.FileOperations.Safety;

public static class DeletionSafetyPolicy
{
    private static readonly Lazy<HashSet<string>> ProtectedDirectories = new(InitializeProtectedDirectories);

    public static DeletionSafetyResult Evaluate(string? rawPath)
    {
        // 8.1 空路径检查
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return DeletionSafetyResult.Denied(DeletionRejectionReason.EmptyOrWhitespacePath, "Path cannot be null, empty, or whitespace.");
        }

        // 8.3 设备路径检查 (\\?\ 或 \\.\)
        if (rawPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            rawPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            rawPath.StartsWith("//?/", StringComparison.Ordinal) ||
            rawPath.StartsWith("//./", StringComparison.Ordinal))
        {
            return DeletionSafetyResult.Denied(DeletionRejectionReason.ExtendedDevicePath, "Extended device paths (\\\\?\\ or \\\\.\\) are prohibited in V1.");
        }

        // 8.4 UNC 网络路径检查 (\\server 或 //server)
        if (rawPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            rawPath.StartsWith("//", StringComparison.Ordinal))
        {
            return DeletionSafetyResult.Denied(DeletionRejectionReason.UncPath, "UNC network paths are prohibited in V1.");
        }

        // 8.8 Alternate Data Stream (ADS) 检查
        int firstColon = rawPath.IndexOf(':');
        if (firstColon >= 0)
        {
            if (firstColon != 1 || !char.IsAsciiLetter(rawPath[0]) || rawPath.IndexOf(':', 2) >= 0)
            {
                return DeletionSafetyResult.Denied(DeletionRejectionReason.AlternateDataStream, "Alternate data streams or malformed drive colons are prohibited.");
            }
        }

        // 8.2 相对路径检查
        if (!Path.IsPathRooted(rawPath))
        {
            return DeletionSafetyResult.Denied(DeletionRejectionReason.NotRootedOrRelativePath, "Relative paths are prohibited; deletion API requires normalized absolute paths.");
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(rawPath);
        }
        catch (Exception ex)
        {
            return DeletionSafetyResult.Denied(DeletionRejectionReason.SecurityCheckException, $"Failed to normalize path: {ex.Message}");
        }

        // 8.5 驱动器根目录检查 (C:\, D:\ 等)
        string? root = Path.GetPathRoot(normalized);
        if (!string.IsNullOrEmpty(root))
        {
            string trimmedNormalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedNormalized, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return DeletionSafetyResult.Denied(DeletionRejectionReason.DriveRootDirectory, $"Deletion of drive root directory '{root}' is prohibited.");
            }
        }

        // 8.6 系统保护目录检查
        foreach (var protectedDir in ProtectedDirectories.Value)
        {
            if (string.Equals(normalized, protectedDir, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(protectedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return DeletionSafetyResult.Denied(DeletionRejectionReason.SystemProtectedDirectory, $"Path '{normalized}' is within system protected directory '{protectedDir}'.");
            }
        }

        string fileName = Path.GetFileName(normalized);
        if (string.Equals(fileName, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "System Volume Information", StringComparison.OrdinalIgnoreCase))
        {
            return DeletionSafetyResult.Denied(DeletionRejectionReason.SystemProtectedDirectory, "System Volume Information and $Recycle.Bin are protected system directories.");
        }

        if (normalized.Contains(Path.DirectorySeparatorChar + "$Recycle.Bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(Path.DirectorySeparatorChar + "System Volume Information" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return DeletionSafetyResult.Denied(DeletionRejectionReason.SystemProtectedDirectory, "Deletion within System Volume Information or $Recycle.Bin is prohibited.");
        }

        // 8.7 ReparsePoint / Junction / SymbolicLink 深度检查 (自身及所有父目录)
        try
        {
            if (File.Exists(normalized))
            {
                var fi = new FileInfo(normalized);
                if (IsReparsePointOrLink(fi.Attributes, fi.LinkTarget))
                {
                    return DeletionSafetyResult.Denied(DeletionRejectionReason.ReparsePointOrLinkDetected, $"Target file '{normalized}' is a ReparsePoint or Link.");
                }
            }
            else if (Directory.Exists(normalized))
            {
                var di = new DirectoryInfo(normalized);
                if (IsReparsePointOrLink(di.Attributes, di.LinkTarget))
                {
                    return DeletionSafetyResult.Denied(DeletionRejectionReason.ReparsePointOrLinkDetected, $"Target directory '{normalized}' is a ReparsePoint or Link.");
                }
            }

            var currentParent = Directory.GetParent(normalized);
            while (currentParent != null)
            {
                if (currentParent.Exists && IsReparsePointOrLink(currentParent.Attributes, currentParent.LinkTarget))
                {
                    return DeletionSafetyResult.Denied(DeletionRejectionReason.ReparsePointOrLinkDetected, $"Ancestor directory '{currentParent.FullName}' is a ReparsePoint or Link.");
                }
                currentParent = currentParent.Parent;
            }
        }
        catch (Exception ex)
        {
            // 8.9 安全检查异常：Fail Closed 闭门拒绝
            return DeletionSafetyResult.Denied(DeletionRejectionReason.SecurityCheckException, $"Security check exception inspecting filesystem attributes: {ex.Message}");
        }

        return DeletionSafetyResult.Allowed();
    }

    private static bool IsReparsePointOrLink(FileAttributes attributes, string? linkTarget)
    {
        return (attributes & FileAttributes.ReparsePoint) != 0 || linkTarget != null;
    }

    private static HashSet<string> InitializeProtectedDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData
        ];

        foreach (var folder in folders)
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    set.Add(Path.GetFullPath(path));
                }
            }
            catch
            {
            }
        }

        return set;
    }
}
