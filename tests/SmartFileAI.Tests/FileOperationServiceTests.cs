using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;
using SmartFileAI.Database;
using SmartFileAI.FileOperations;
using Xunit;

namespace SmartFileAI.Tests;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _dbPath;
    private readonly FileRepository _repository;
    private readonly FileOperationService _service;

    public FileOperationServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SmartFileAI_OpTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _dbPath = Path.Combine(Path.GetTempPath(), $"SmartFileAI_OpDb_{Guid.NewGuid():N}.db");
        using var db = new SmartFileAIDbContext(_dbPath);
        db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new FileRepository(_dbPath);
        _service = new FileOperationService(_repository);
    }

    [Fact]
    public async Task PermanentDelete_ExistingFile_ShouldDeletePhysicallyAndReturnSuccess()
    {
        string file = Path.Combine(_testRoot, "delete_me.txt");
        await File.WriteAllTextAsync(file, "test");
        var result = await _service.DeleteAsync(file, FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.Success, result);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task PermanentDelete_ReadOnlyFile_ShouldClearAttributeAndSucceed()
    {
        string file = Path.Combine(_testRoot, "readonly.txt");
        await File.WriteAllTextAsync(file, "test");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var result = await _service.DeleteAsync(file, FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.Success, result);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task PermanentDelete_Directory_ShouldDeleteRecursivelyWithinSandbox()
    {
        string dir = Path.Combine(_testRoot, "dir");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a");
        var result = await _service.DeleteAsync(dir, FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.Success, result);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task DeleteAsync_LockedFile_ShouldReturnFileLocked()
    {
        string file = Path.Combine(_testRoot, "locked.dat");
        await File.WriteAllTextAsync(file, "locked");
        using var stream = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await _service.DeleteAsync(file, FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.FileLocked, result);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentFile_ShouldReturnNotFound()
    {
        var result = await _service.DeleteAsync(Path.Combine(_testRoot, "missing.txt"), FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.NotFound, result);
    }

    [Fact]
    public async Task DeleteAsync_WithRepository_ShouldRemoveIndexAndWriteLog()
    {
        string file = Path.Combine(_testRoot, "sync.txt");
        await File.WriteAllTextAsync(file, "sync");
        await _repository.BulkInsertOrUpdateAsync(new[] { new FileItem
        {
            Name = "sync.txt", FullPath = file, ParentPath = _testRoot, Size = 4, Extension = ".txt",
            CreatedTime = DateTime.UtcNow, ModifiedTime = DateTime.UtcNow, Attributes = FileAttributes.Normal
        }});

        var result = await _service.DeleteAsync(file, FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.Success, result);
        Assert.Empty(await _repository.GetFilesByDirectoryAsync(_testRoot));
        
        await using var db = new SmartFileAIDbContext(_dbPath);
        Assert.True(await db.OperationLogs.AnyAsync(x => x.Path == Path.GetFullPath(file) && x.Result == OperationResultType.Success));
    }

    [Fact]
    public async Task DeleteAsync_SafetyPolicyViolation_ShouldReturnPermissionDeniedAndNotTouchDiskOrDb()
    {
        var spyRepo = new SpyFileRepository();
        var hardenedService = new FileOperationService(spyRepo);

        var result = await hardenedService.DeleteAsync(@"C:\", FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.PermissionDenied, result);
        Assert.False(spyRepo.DeleteFileRecordCalled);
    }

    [Fact]
    public async Task DeleteAsync_PhysicalSuccessButRepoDeleteFails_ShouldReturnSuccessWithWarning()
    {
        string file = Path.Combine(_testRoot, "warning_test.txt");
        await File.WriteAllTextAsync(file, "test data");

        var throwingRepo = new ThrowingFileRepository(throwOnDelete: true, throwOnLog: false);
        var serviceWithWarning = new FileOperationService(throwingRepo);

        var result = await serviceWithWarning.DeleteAsync(file, FileOperationType.PermanentDelete);

        Assert.Equal(OperationResultType.SuccessWithWarning, result);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task DeleteAsync_PhysicalSuccessButLogFails_ShouldReturnSuccessWithWarning()
    {
        string file = Path.Combine(_testRoot, "log_warning_test.txt");
        await File.WriteAllTextAsync(file, "test data");

        var throwingRepo = new ThrowingFileRepository(throwOnDelete: false, throwOnLog: true);
        var serviceWithWarning = new FileOperationService(throwingRepo);

        var result = await serviceWithWarning.DeleteAsync(file, FileOperationType.PermanentDelete);

        Assert.Equal(OperationResultType.SuccessWithWarning, result);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task DeleteAsync_ProhibitedPaths_ShouldReturnPermissionDenied()
    {
        var resUnc = await _service.DeleteAsync(@"\\server\share\file.txt", FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.PermissionDenied, resUnc);

        var resDevice = await _service.DeleteAsync(@"\\?\C:\test.txt", FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.PermissionDenied, resDevice);

        var resAds = await _service.DeleteAsync(@"C:\temp\file.txt:stream", FileOperationType.PermanentDelete);
        Assert.Equal(OperationResultType.PermissionDenied, resAds);
    }

    public void Dispose()
    {
        _repository.Dispose();
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true); } catch { }
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private sealed class SpyFileRepository : IFileRepository
    {
        public bool DeleteFileRecordCalled { get; private set; }

        public Task BulkInsertOrUpdateAsync(IEnumerable<FileItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IEnumerable<FileItem>> SearchFilesAsync(string keyword, string? extension = null, int limit = 500) => Task.FromResult<IEnumerable<FileItem>>(Array.Empty<FileItem>());
        public Task<IEnumerable<FileItem>> GetFilesByDirectoryAsync(string directoryPath) => Task.FromResult<IEnumerable<FileItem>>(Array.Empty<FileItem>());
        public Task<int> RecordScanStartAsync(string drive) => Task.FromResult(1);
        public Task CompleteScanAsync(int historyId, long scannedFiles, long totalBytes) => Task.CompletedTask;
        public Task DeleteFileRecordAsync(string fullPath)
        {
            DeleteFileRecordCalled = true;
            return Task.CompletedTask;
        }
        public Task LogOperationAsync(OperationLog log) => Task.CompletedTask;
    }

    private sealed class ThrowingFileRepository(bool throwOnDelete, bool throwOnLog) : IFileRepository
    {
        public Task BulkInsertOrUpdateAsync(IEnumerable<FileItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IEnumerable<FileItem>> SearchFilesAsync(string keyword, string? extension = null, int limit = 500) => Task.FromResult<IEnumerable<FileItem>>(Array.Empty<FileItem>());
        public Task<IEnumerable<FileItem>> GetFilesByDirectoryAsync(string directoryPath) => Task.FromResult<IEnumerable<FileItem>>(Array.Empty<FileItem>());
        public Task<int> RecordScanStartAsync(string drive) => Task.FromResult(1);
        public Task CompleteScanAsync(int historyId, long scannedFiles, long totalBytes) => Task.CompletedTask;

        public Task DeleteFileRecordAsync(string fullPath)
        {
            if (throwOnDelete) throw new InvalidOperationException("Simulated SQLite disk/lock failure during record deletion.");
            return Task.CompletedTask;
        }

        public Task LogOperationAsync(OperationLog log)
        {
            if (throwOnLog) throw new InvalidOperationException("Simulated SQLite failure during operation log append.");
            return Task.CompletedTask;
        }
    }
}
