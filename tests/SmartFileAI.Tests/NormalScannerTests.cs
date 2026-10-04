using SmartFileAI.Core.Models;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Scanner;
using Xunit;

namespace SmartFileAI.Tests;

public sealed class NormalScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmartFileAI_ScannerTests_" + Guid.NewGuid().ToString("N"));
    private readonly NormalScanner _scanner = new();

    public NormalScannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ScanDirectoryAsync_ShouldEnumerateFilesAndDirectories()
    {
        string sub = Path.Combine(_root, "Sub");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(_root, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(sub, "child.log"), "child");
        var result = new List<FileItem>();
        await foreach (var item in _scanner.ScanDirectoryAsync(_root)) result.Add(item);
        Assert.Contains(result, x => x.IsDirectory && x.Name == "Sub");
        Assert.Contains(result, x => !x.IsDirectory && x.Name == "root.txt");
        Assert.Contains(result, x => !x.IsDirectory && x.Name == "child.log");
    }

    [Fact]
    public async Task ScanDirectoryAsync_Cancel_ShouldThrowOperationCanceled()
    {
        for (int i = 0; i < 50; i++) await File.WriteAllTextAsync(Path.Combine(_root, $"{i}.txt"), "x");
        using var cts = new CancellationTokenSource();
        int count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _scanner.ScanDirectoryAsync(_root, cancellationToken: cts.Token))
            {
                if (++count == 5) cts.Cancel();
            }
        });
    }

    [Fact]
    public async Task ScanDirectoryAsync_ShouldReportFinalProgress()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "payload.bin"), new string('A', 1024));
        var reports = new List<ScanProgressReport>();
        var progress = new InlineProgress<ScanProgressReport>(reports.Add);
        await foreach (var _ in _scanner.ScanDirectoryAsync(_root, progress)) { }
        Assert.NotEmpty(reports);
        Assert.True(reports[^1].ScannedFilesCount >= 1);
        Assert.True(reports[^1].TotalScannedSizeBytes >= 1024);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
