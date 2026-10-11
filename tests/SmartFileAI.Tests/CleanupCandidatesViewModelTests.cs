using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Cleanup;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;
using SmartFileAI.Database;
using SmartFileAI.FileOperations;
using SmartFileAI.FileOperations.Cleanup;
using SmartFileAI.UI.Services;
using SmartFileAI.UI.ViewModels;
using Xunit;

namespace SmartFileAI.Tests;

public sealed class CleanupCandidatesViewModelTests
{
    [Fact]
    public async Task EmptyScope_DoesNotStart_AndInvalidScopeIsRejected()
    {
        var reader = new FakeReader(); var vm = Create(reader);
        Assert.False(vm.StartAnalysisCommand.CanExecute(null));
        await vm.StartAnalysisAsync();
        Assert.Equal(0, reader.Calls);
        Assert.False(vm.IsRunning);
        Assert.Throws<ArgumentException>(() => vm.InitializeScope("relative"));
    }

    [Theory]
    [InlineData(JunkRiskLevel.Safe, CleanupRecommendation.Recommended, true)]
    [InlineData(JunkRiskLevel.Safe, CleanupRecommendation.NotRecommended, false)]
    [InlineData(JunkRiskLevel.Review, CleanupRecommendation.NeedsReview, false)]
    [InlineData(JunkRiskLevel.Protected, CleanupRecommendation.NotRecommended, false)]
    public void RowSelection_EnforcesSafeAndRecommendedOnly(JunkRiskLevel risk, CleanupRecommendation action, bool eligible)
    {
        var candidate = Candidate(Item(1), risk, action, 17);
        if (risk == JunkRiskLevel.Review) candidate.IsSelected = true; // Foundation permits explicit Review selection; this UI must not.
        var row = new CleanupCandidateRowViewModel(candidate);
        Assert.Equal(eligible, row.IsEligibleForSelection);
        Assert.Equal(eligible, row.IsSelected);
        row.IsSelected = false;
        Assert.False(candidate.IsSelected);
        row.IsSelected = true;
        Assert.Equal(eligible, row.IsSelected);
        Assert.Equal(eligible, candidate.IsSelected);
    }

    [Fact]
    public async Task CountsAndSelectionBytes_UseReclaimableBytesRatherThanIndexSize()
    {
        var reader = new FakeReader { Items = new[] { Item(1), Item(2), Item(3), Item(4) } };
        var analyzer = new FakeAnalyzer((item, _) => item.Id switch
        {
            1 => Candidate(item, JunkRiskLevel.Safe, CleanupRecommendation.Recommended, 7),
            2 => Candidate(item, JunkRiskLevel.Safe, CleanupRecommendation.Recommended, 13),
            3 => Candidate(item, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview, 101),
            _ => Candidate(item, JunkRiskLevel.Protected, CleanupRecommendation.NotRecommended, 999)
        });
        var vm = Create(reader, analyzer); vm.InitializeScope(@"D:\Docs");
        await vm.StartAnalysisAsync();
        Assert.Equal(4, vm.AnalyzedCount); Assert.Equal(4, vm.CandidateCount);
        Assert.Equal(2, vm.SafeCount); Assert.Equal(1, vm.ReviewCount); Assert.Equal(1, vm.ProtectedCount);
        Assert.Equal(2, vm.SelectedCount); Assert.Equal(20m, vm.SelectedEstimatedBytes);
        vm.Candidates[0].IsSelected = false;
        vm.Candidates[2].IsSelected = true; vm.Candidates[3].IsSelected = true;
        Assert.Equal(1, vm.SelectedCount); Assert.Equal(13m, vm.SelectedEstimatedBytes);
        Assert.False(vm.IsPartial); Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task SelectionTotal_DoesNotOverflowLong()
    {
        var vm = Create(new FakeReader { Items = new[] { Item(1), Item(2) } },
            new FakeAnalyzer((item, _) => Candidate(item, JunkRiskLevel.Safe, CleanupRecommendation.Recommended, long.MaxValue)));
        vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Equal(2m * long.MaxValue, vm.SelectedEstimatedBytes);
    }

    [Fact]
    public async Task RepeatedAnalysis_ClearsOldRowsAndIsolatesSelection()
    {
        var reader = new FakeReader { Items = new[] { Item(1) } };
        var vm = Create(reader); vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        var old = Assert.Single(vm.Candidates);
        reader.Items = new[] { Item(2), Item(3) };
        vm.InitializeScope(@"D:\Other"); await vm.StartAnalysisAsync();
        Assert.Equal(new long[] { 2, 3 }, vm.Candidates.Select(x => x.Candidate.Item.Id));
        Assert.Equal(2, vm.AnalyzedCount); Assert.Equal(2, vm.SelectedCount);
        old.IsSelected = false;
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(@"D:\Other", reader.Scopes.Last());
    }

    [Fact]
    public async Task CandidateCap_StopsAnalysisAndClearlyReportsPartialResults()
    {
        var analyzer = new FakeAnalyzer();
        var vm = Create(new FakeReader { Items = Enumerable.Range(1, 1000).Select(x => Item(x)).ToArray() }, analyzer, limit: 3);
        vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Equal(3, vm.CandidateCount); Assert.Equal(3, vm.AnalyzedCount); Assert.Equal(3, analyzer.Calls);
        Assert.True(vm.IsPartial); Assert.Contains("部分结果", vm.StatusMessage); Assert.Contains("未完成", vm.StatusMessage);
    }

    [Fact]
    public async Task RealDefaultCap_RemainsBoundedAtFiveThousandAcrossManyPages()
    {
        var analyzer = new FakeAnalyzer(); var reader = new FakeReader { Items = Enumerable.Range(1, 6000).Select(x => Item(x)).ToArray() };
        var vm = Create(reader, analyzer); vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Equal(5000, vm.Candidates.Count); Assert.Equal(5000, analyzer.Calls);
        Assert.Equal(20, reader.Calls); Assert.True(vm.IsPartial);
    }

    [Fact]
    public async Task ReferenceTime_IsOneStableUtcValueForEveryPage()
    {
        var analyzer = new FakeAnalyzer();
        var vm = Create(new FakeReader { Items = Enumerable.Range(1, 600).Select(x => Item(x)).ToArray() }, analyzer);
        vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Equal(600, analyzer.Times.Count);
        Assert.Single(analyzer.Times.Distinct());
        Assert.All(analyzer.Times, time => Assert.Equal(DateTimeKind.Utc, time.Kind));
    }

    [Fact]
    public async Task UiNotifications_AreBatchedRatherThanDispatchedPerItem()
    {
        var scheduler = new ImmediateScheduler();
        var vm = Create(new FakeReader { Items = Enumerable.Range(1, 256).Select(x => Item(x)).ToArray() }, scheduler: scheduler);
        int notifications = 0;
        ((INotifyCollectionChanged)vm.Candidates).CollectionChanged += (_, _) => notifications++;
        vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Equal(256, vm.CandidateCount);
        Assert.Equal(5, notifications); // one clear + four 64-row Reset notifications
        Assert.Equal(6, scheduler.Calls); // four batches, terminal status and final running state
    }

    [Fact]
    public async Task Cancellation_StopsReaderRetainsCompletedBatchesAndAllowsNextRun()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader { Items = new[] { Item(1) }, OnRead = async (cursor, token) =>
        {
            if (cursor > 0) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        }};
        var vm = Create(reader); vm.InitializeScope(@"D:\Docs");
        Task run = vm.StartAnalysisAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsRunning); Assert.True(vm.CancelAnalysisCommand.CanExecute(null));
        Assert.False(vm.StartAnalysisCommand.CanExecute(null));
        await vm.StartAnalysisAsync(); Assert.Equal(2, reader.Calls); // no concurrent second run
        Assert.Throws<InvalidOperationException>(() => vm.InitializeScope(@"D:\Other"));
        vm.CancelAnalysis(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsRunning); Assert.True(vm.IsPartial); Assert.Equal(1, vm.CandidateCount);
        Assert.Contains("取消", vm.StatusMessage);
        reader.OnRead = null; reader.Items = new[] { Item(2) };
        await vm.StartAnalysisAsync();
        Assert.Equal(2, Assert.Single(vm.Candidates).Candidate.Item.Id); Assert.False(vm.IsPartial);
    }

    [Fact]
    public async Task CancelDuringUnpublishedBatch_PreservesActualCompletedCount()
    {
        CleanupCandidatesViewModel? vm = null;
        var analyzer = new FakeAnalyzer((item, _) =>
        {
            if (item.Id == 2) vm!.CancelAnalysis();
            return Candidate(item, JunkRiskLevel.Safe, CleanupRecommendation.Recommended, 1);
        });
        vm = Create(new FakeReader { Items = new[] { Item(1), Item(2), Item(3) } }, analyzer);
        vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Equal(2, vm.AnalyzedCount); Assert.Equal(2, vm.CandidateCount); Assert.True(vm.IsPartial);
    }

    [Fact]
    public async Task WindowClose_CancelsAndSuppressesLateResultsEvenWhenReaderIgnoresCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader { Items = new[] { Item(1) }, OnRead = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        var vm = Create(reader); vm.InitializeScope(@"D:\Docs"); Task run = vm.StartAnalysisAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.OnWindowClosing(); release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(vm.Candidates); Assert.Equal(0, vm.AnalyzedCount); Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CanStartAnalysis);
        await vm.StartAnalysisAsync(); Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task ReaderOrAnalyzerErrors_AreObservedAndNeverReportedAsComplete()
    {
        var reader = new FakeReader { OnRead = (_, _) => throw new IOException("test index failure") };
        var vm = Create(reader); vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.True(vm.IsPartial); Assert.False(vm.IsRunning); Assert.Contains("test index failure", vm.StatusMessage);
        reader.OnRead = null; reader.Items = new[] { Item(1) };
        vm = Create(reader, new FakeAnalyzer((_, _) => throw new IOException("test analyzer failure")));
        vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.True(vm.IsPartial); Assert.False(vm.IsRunning); Assert.Contains("test analyzer failure", vm.StatusMessage);
    }

    [Fact]
    public async Task BrokenReaderCursor_FailsRatherThanLoopingForever()
    {
        var reader = new FakeReader { Items = new[] { Item(0) }, IgnoreCursor = true };
        var vm = Create(reader); vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.True(vm.IsPartial); Assert.Contains("严格递增", vm.StatusMessage); Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task EmptyIndex_CompletesWithZeroCounts()
    {
        var vm = Create(new FakeReader()); vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Empty(vm.Candidates); Assert.Equal(0, vm.AnalyzedCount); Assert.Equal(0m, vm.SelectedEstimatedBytes);
        Assert.False(vm.IsPartial); Assert.Contains("完成", vm.StatusMessage);
    }

    [Fact]
    public async Task NonCandidateRows_AreStillCountedAndPagedWithoutAccumulation()
    {
        var reader = new FakeReader { Items = Enumerable.Range(1, 600).Select(x => Item(x)).ToArray() };
        var analyzer = new FakeAnalyzer((_, _) => null);
        var vm = Create(reader, analyzer); vm.InitializeScope(@"D:\Docs"); await vm.StartAnalysisAsync();
        Assert.Equal(600, vm.AnalyzedCount); Assert.Equal(600, analyzer.Calls);
        Assert.Equal(4, reader.Calls); Assert.Empty(vm.Candidates); Assert.False(vm.IsPartial);
    }

    [Fact]
    public async Task QueuedUiBatch_CannotRepopulateClosedWindow()
    {
        var scheduler = new QueuedScheduler();
        var vm = new CleanupCandidatesViewModel(new FakeReader { Items = new[] { Item(1) } }, new FakeAnalyzer(), scheduler);
        vm.InitializeScope(@"D:\Docs"); Task run = vm.StartAnalysisAsync();
        await scheduler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.OnWindowClosing(); scheduler.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(vm.Candidates); Assert.Equal(0, vm.AnalyzedCount);
        Assert.Equal(0, vm.SelectedCount); Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task ActualSqliteAndAnalyzerIntegration_IsReadOnlyAndNeverUsesDeleteService()
    {
        string root = Path.Combine(Path.GetTempPath(), "SmartFileAI_ExplorerIntegration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "old.tmp"); File.WriteAllText(path, "payload");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
            string dbPath = Path.Combine(root, "index.db");
            await using (var db = new SmartFileAIDbContext(dbPath))
            {
                await db.InitializeDatabaseAsync();
                db.Files.Add(new FileItem { Name = "old.tmp", FullPath = path, ParentPath = root, Size = 999,
                    Extension = ".tmp", ModifiedTime = File.GetLastWriteTimeUtc(path), Attributes = File.GetAttributes(path) });
                await db.SaveChangesAsync();
            }
            var spy = new NoDeletionService();
            var vm = new CleanupCandidatesViewModel(new SqliteCleanupIndexReader(dbPath), new RuleBasedCleanupAnalyzer(spy), new ImmediateScheduler());
            vm.InitializeScope(root); await vm.StartAnalysisAsync();
            var row = Assert.Single(vm.Candidates);
            Assert.True(row.IsEligibleForSelection); Assert.Equal(7m, vm.SelectedEstimatedBytes);
            Assert.Equal(1, spy.LockCalls); Assert.Equal(0, spy.DeleteCalls);
            Assert.Equal("payload", File.ReadAllText(path));
            Assert.NotEmpty(row.Evidence); Assert.Contains("恢复", row.ReversibilityExplanation);
            await using var verify = new SmartFileAIDbContext(dbPath);
            Assert.Equal(1, verify.Files.Count()); Assert.Equal(0, verify.OperationLogs.Count());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RealDiRegistrations_ResolveIndependentWindowViewModelsAndExistingAnalyzer()
    {
        var services = new ServiceCollection(); var spy = new NoDeletionService();
        services.AddSingleton<IFileOperationService>(spy);
        services.AddCleanupExplorer(Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid().ToString("N") + ".db"), new ImmediateScheduler());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var first = provider.GetRequiredService<CleanupCandidatesViewModel>();
        var second = provider.GetRequiredService<CleanupCandidatesViewModel>();
        Assert.NotSame(first, second);
        Assert.IsType<SqliteCleanupIndexReader>(provider.GetRequiredService<ICleanupIndexReader>());
        Assert.IsType<RuleBasedCleanupAnalyzer>(provider.GetRequiredService<ICleanupAnalyzer>());
        Assert.Equal(0, spy.DeleteCalls); Assert.Equal(0, spy.LockCalls);
    }

    private static FileItem Item(long id) => new() { Id = id, FullPath = $@"D:\Docs\item{id}.tmp", Name = $"item{id}.tmp", Size = 999 };
    private static CleanupCandidate Candidate(FileItem item, JunkRiskLevel risk, CleanupRecommendation action, long bytes)
        => new(item, JunkCategory.TemporaryFile, risk, action, "测试分析原因", new[] { "测试证据" }, bytes);
    private static CleanupCandidatesViewModel Create(FakeReader reader, FakeAnalyzer? analyzer = null, int limit = 5000, ImmediateScheduler? scheduler = null)
        => new(reader, analyzer ?? new FakeAnalyzer(), scheduler ?? new ImmediateScheduler(), limit);

    private sealed class FakeReader : ICleanupIndexReader
    {
        public IReadOnlyList<FileItem> Items { get; set; } = Array.Empty<FileItem>();
        public int Calls { get; private set; }
        public List<string> Scopes { get; } = new();
        public bool IgnoreCursor { get; init; }
        public Func<long, CancellationToken, Task>? OnRead { get; set; }
        public async Task<IReadOnlyList<FileItem>> GetIndexPageAsync(string scope, long cursor, int size, CancellationToken token = default)
        {
            Calls++; Scopes.Add(scope);
            if (OnRead is not null) await OnRead(cursor, token);
            return Items.Where(x => IgnoreCursor || x.Id > cursor).OrderBy(x => x.Id).Take(size).ToArray();
        }
    }
    private sealed class FakeAnalyzer(Func<FileItem, DateTime, CleanupCandidate?>? rule = null) : ICleanupAnalyzer
    {
        public int Calls { get; private set; }
        public List<DateTime> Times { get; } = new();
        public CleanupCandidate? Analyze(FileItem item, DateTime time)
        {
            Calls++; Times.Add(time);
            return rule is null ? Candidate(item, JunkRiskLevel.Safe, CleanupRecommendation.Recommended, 1) : rule(item, time);
        }
    }
    private sealed class ImmediateScheduler : ICleanupUiScheduler
    {
        public int Calls { get; private set; }
        public Task InvokeAsync(Action action, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++; action(); return Task.CompletedTask;
        }
    }
    private sealed class QueuedScheduler : ICleanupUiScheduler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task InvokeAsync(Action action, CancellationToken token = default)
        {
            Entered.TrySetResult(); await Release.Task;
            action(); // Deliberately does not honor cancellation: the run/close guard must reject stale publication.
        }
    }
    private sealed class NoDeletionService : IFileOperationService
    {
        private readonly FileOperationService _real = new();
        public int LockCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool IsFileLocked(string path) { LockCalls++; return _real.IsFileLocked(path); }
        public Task<OperationResultType> DeleteAsync(string path, FileOperationType operation)
        {
            DeleteCalls++; throw new InvalidOperationException("Read-only explorer attempted deletion.");
        }
    }
}
