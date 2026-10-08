using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;

namespace SmartFileAI.UI.ViewModels;

public sealed record SortOption(FileSortMode Mode, string DisplayName);

public partial class MainViewModel : ObservableObject
{
    private readonly IFileScanner _scanner;
    private readonly IFileRepository _repository;
    private readonly IFileOperationService _fileOperationService;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty] private ObservableCollection<DirectoryNodeViewModel> _rootDrives = new();
    [ObservableProperty] private DirectoryNodeViewModel? _selectedNode;
    [ObservableProperty] private ObservableCollection<FileItem> _currentFiles = new();
    [ObservableProperty] private FileItem? _selectedFile;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private long _scannedFileCount;
    [ObservableProperty] private long _scannedDirCount;
    [ObservableProperty] private long _scannedTotalBytes;
    [ObservableProperty] private string _searchKeyword = string.Empty;
    [ObservableProperty] private FileSortMode _selectedSortMode = FileSortMode.NameAscending;

    public IReadOnlyList<SortOption> SortOptions { get; } = new[]
    {
        new SortOption(FileSortMode.NameAscending, "名称"),
        new SortOption(FileSortMode.ModifiedNewest, "修改时间：最新优先"),
        new SortOption(FileSortMode.ModifiedOldest, "修改时间：最旧优先")
    };

    public ObservableCollection<DirectoryNodeViewModel> Drives => RootDrives;

    public MainViewModel(IFileScanner scanner, IFileRepository repository, IFileOperationService fileOperationService)
    {
        _scanner = scanner;
        _repository = repository;
        _fileOperationService = fileOperationService;
        LoadDrives();
    }

    public void LoadDrives()
    {
        RootDrives.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
            RootDrives.Add(new DirectoryNodeViewModel($"{label} ({drive.Name.TrimEnd('\')})", drive.RootDirectory.FullName, true));
        }
    }

    partial void OnSelectedNodeChanged(DirectoryNodeViewModel? value)
    {
        if (!IsUsableDirectoryNode(value)) return;

        if (string.IsNullOrWhiteSpace(SearchKeyword))
            _ = LoadFilesSafelyAsync(value!.FullPath);
        else
            _ = SearchAsync();
    }

    partial void OnSelectedSortModeChanged(FileSortMode value)
    {
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
            _ = SearchAsync();
        else if (IsUsableDirectoryNode(SelectedNode))
            _ = LoadFilesSafelyAsync(SelectedNode!.FullPath);
    }

    private static bool IsUsableDirectoryNode(DirectoryNodeViewModel? node)
        => node is not null
           && !string.IsNullOrWhiteSpace(node.FullPath)
           && !string.Equals(node.Name, "Loading...", StringComparison.Ordinal);

    private async Task LoadFilesSafelyAsync(string path)
    {
        try { await LoadFilesForDirectoryAsync(path); }
        catch (Exception ex) { StatusMessage = $"目录加载失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task LoadFilesForDirectoryAsync(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            StatusMessage = "目录加载失败: 路径为空";
            return;
        }

        var sortMode = SelectedSortMode;
        var dbFiles = (await _repository.GetFilesByDirectoryAsync(directoryPath, sortMode)).ToList();
        List<FileItem> items = dbFiles;
        if (items.Count == 0 && Directory.Exists(directoryPath))
        {
            items = ApplyLocalSorting(await Task.Run(() => EnumerateDirectorySnapshot(directoryPath)), sortMode).ToList();
        }

        if (sortMode != SelectedSortMode) return;
        CurrentFiles.Clear();
        foreach (var item in items) CurrentFiles.Add(item);
        StatusMessage = $"已加载 {CurrentFiles.Count:N0} 个项目";
    }

    private static List<FileItem> EnumerateDirectorySnapshot(string directoryPath)
    {
        var result = new List<FileItem>();
        try
        {
            foreach (var entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos())
            {
                try
                {
                    bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    result.Add(new FileItem
                    {
                        Name = entry.Name,
                        FullPath = entry.FullName,
                        ParentPath = directoryPath,
                        IsDirectory = isDir,
                        Size = isDir ? 0 : ((FileInfo)entry).Length,
                        Extension = isDir ? string.Empty : entry.Extension.ToLowerInvariant(),
                        CreatedTime = entry.CreationTime,
                        ModifiedTime = entry.LastWriteTime,
                        Attributes = entry.Attributes
                    });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
        }
        return result.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IOrderedEnumerable<FileItem> ApplyLocalSorting(IEnumerable<FileItem> items, FileSortMode sortMode)
        => sortMode switch
        {
            FileSortMode.ModifiedNewest => items.OrderByDescending(f => f.IsDirectory)
                .ThenByDescending(f => f.ModifiedTime).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            FileSortMode.ModifiedOldest => items.OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.ModifiedTime).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        };

    [RelayCommand]
    public async Task SearchAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SearchKeyword))
            {
                if (IsUsableDirectoryNode(SelectedNode))
                    await LoadFilesForDirectoryAsync(SelectedNode!.FullPath);
                else
                    CurrentFiles.Clear();
                return;
            }

            string? scopePath = IsUsableDirectoryNode(SelectedNode) ? SelectedNode!.FullPath : null;
            var sortMode = SelectedSortMode;
            var results = await _repository.SearchFilesAsync(SearchKeyword.Trim(), null, 500, scopePath, sortMode);
            if (sortMode != SelectedSortMode) return;

            CurrentFiles.Clear();
            foreach (var file in results) CurrentFiles.Add(file);

            string scopeText = scopePath is null ? "全局索引" : scopePath;
            StatusMessage = $"搜索完成: {CurrentFiles.Count:N0} 个结果（范围: {scopeText}）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StartScanAsync()
    {
        if (IsScanning) return;
        if (SelectedNode is null && RootDrives.Count > 0) SelectedNode = RootDrives[0];
        string? targetPath = SelectedNode?.FullPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            StatusMessage = "没有可扫描的磁盘或目录";
            return;
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        IsScanning = true;
        ScannedFileCount = ScannedDirCount = ScannedTotalBytes = 0;
        StatusMessage = $"正在扫描: {targetPath}";
        int? scanHistoryId = null;

        var progress = new Progress<ScanProgressReport>(report =>
        {
            ScannedFileCount = report.ScannedFilesCount;
            ScannedDirCount = report.ScannedDirectoriesCount;
            ScannedTotalBytes = report.TotalScannedSizeBytes;
            StatusMessage = report.WarningMessage is null ? $"扫描中: {report.CurrentDirectory}" : $"跳过: {report.WarningMessage}";
        });

        try
        {
            scanHistoryId = await _repository.RecordScanStartAsync(targetPath);
            await Task.Run(async () =>
            {
                var buffer = new List<FileItem>(1000);
                await foreach (var item in _scanner.ScanDirectoryAsync(targetPath, progress, token))
                {
                    buffer.Add(item);
                    if (buffer.Count >= 1000)
                    {
                        await _repository.BulkInsertOrUpdateAsync(buffer, token);
                        buffer.Clear();
                    }
                }
                if (buffer.Count > 0) await _repository.BulkInsertOrUpdateAsync(buffer, token);
            }, token);

            if (scanHistoryId.HasValue)
                await _repository.CompleteScanAsync(scanHistoryId.Value, ScannedFileCount, ScannedTotalBytes);

            StatusMessage = $"扫描完成: 索引 {ScannedFileCount:N0} 个文件";
            if (SelectedNode is not null) await LoadFilesForDirectoryAsync(SelectedNode.FullPath);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    public void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    public async Task DeleteSelectedFileAsync(string? mode)
    {
        if (SelectedFile is null) return;
        bool permanent = string.Equals(mode, "Permanent", StringComparison.OrdinalIgnoreCase);
        var operation = permanent ? FileOperationType.PermanentDelete : FileOperationType.RecycleDelete;
        var target = SelectedFile;

        string text = permanent
            ? $"即将永久删除：
{target.FullPath}

此操作不可恢复。是否继续？"
            : $"将以下项目移入回收站：
{target.FullPath}

是否继续？";
        string title = permanent ? "确认永久删除" : "确认移入回收站";
        if (MessageBox.Show(text, title, MessageBoxButton.YesNo, permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            var result = await _fileOperationService.DeleteAsync(target.FullPath, operation);
            switch (result)
            {
                case OperationResultType.Success:
                    CurrentFiles.Remove(target);
                    SelectedFile = null;
                    StatusMessage = $"已删除: {target.Name}";
                    break;

                case OperationResultType.SuccessWithWarning:
                    CurrentFiles.Remove(target);
                    SelectedFile = null;
                    StatusMessage = $"文件已删除，但索引或审计日志同步异常，建议重新扫描 ({target.Name})";
                    break;

                case OperationResultType.PermissionDenied:
                    StatusMessage = $"安全策略已阻止删除: {target.Name}";
                    break;

                case OperationResultType.FileLocked:
                    StatusMessage = $"删除未完成: 文件被占用或锁定 ({target.Name})";
                    break;

                case OperationResultType.NotFound:
                    StatusMessage = $"删除未完成: 目标不存在 ({target.Name})";
                    break;

                case OperationResultType.Failed:
                default:
                    StatusMessage = $"删除失败: {target.Name}";
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }
}
