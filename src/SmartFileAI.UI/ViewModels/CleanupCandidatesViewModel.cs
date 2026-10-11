using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Cleanup;
using SmartFileAI.Core.Models.Enums;
using SmartFileAI.UI.Services;

namespace SmartFileAI.UI.ViewModels;

public partial class CleanupCandidatesViewModel : ObservableObject
{
    public const int DefaultCandidateLimit = 5000;
    private const int PageSize = 256;
    private const int UiBatchSize = 64;
    private readonly ICleanupIndexReader _reader;
    private readonly ICleanupAnalyzer _analyzer;
    private readonly ICleanupUiScheduler _ui;
    private readonly int _candidateLimit;
    private readonly CleanupCandidateCollection _rows = new();
    private CancellationTokenSource? _cts;
    private long _runId;
    private volatile bool _closed;

    private string _scopePath = string.Empty;
    public string ScopePath
    {
        get => _scopePath;
        private set { if (SetProperty(ref _scopePath, value)) StartAnalysisCommand.NotifyCanExecuteChanged(); }
    }
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPartial;
    [ObservableProperty] private string _statusMessage = "请选择目录或驱动器。";
    [ObservableProperty] private long _analyzedCount;
    [ObservableProperty] private int _candidateCount;
    [ObservableProperty] private int _safeCount;
    [ObservableProperty] private int _reviewCount;
    [ObservableProperty] private int _protectedCount;
    [ObservableProperty] private int _selectedCount;
    // Up to 5000 long byte sizes can exceed long.MaxValue. Decimal keeps the total exact.
    [ObservableProperty] private decimal _selectedEstimatedBytes;
    [ObservableProperty] private CleanupCandidateRowViewModel? _selectedCandidate;
    public ReadOnlyObservableCollection<CleanupCandidateRowViewModel> Candidates { get; }
    public bool CanStartAnalysis => !_closed && !IsRunning && !string.IsNullOrWhiteSpace(ScopePath);
    public bool CanCancelAnalysis => !_closed && IsRunning;

    public CleanupCandidatesViewModel(ICleanupIndexReader reader, ICleanupAnalyzer analyzer,
        ICleanupUiScheduler ui, int candidateLimit = DefaultCandidateLimit)
    {
        if (candidateLimit is < 1 or > DefaultCandidateLimit) throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(ui);
        _reader = reader; _analyzer = analyzer; _ui = ui; _candidateLimit = candidateLimit;
        Candidates = new ReadOnlyObservableCollection<CleanupCandidateRowViewModel>(_rows);
    }

    public void InitializeScope(string scope)
    {
        if (_closed) throw new ObjectDisposedException(nameof(CleanupCandidatesViewModel));
        if (IsRunning) throw new InvalidOperationException("分析期间不能更改范围。");
        ScopePath = CleanupScopePath.Normalize(scope);
        StatusMessage = $"分析范围：{ScopePath}";
    }
    partial void OnIsRunningChanged(bool value)
    {
        StartAnalysisCommand.NotifyCanExecuteChanged();
        CancelAnalysisCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStartAnalysis))]
    public async Task StartAnalysisAsync()
    {
        if (!CanStartAnalysis) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        long run = Interlocked.Increment(ref _runId);
        string scope = ScopePath; // immutable for this run, even if the main selection changes
        DateTime referenceTime = DateTime.UtcNow;
        try
        {
            IsRunning = true;
            ResetRows();
            StatusMessage = "正在分析索引；不会删除文件。";
            bool capped = await Task.Run(() => RunAsync(scope, referenceTime, run, cts.Token), cts.Token).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            await PublishAsync(run, () =>
            {
                IsPartial = capped;
                StatusMessage = capped ? $"已达到 {_candidateLimit:N0} 个候选显示上限；仅为部分结果，未完成全范围分析。"
                    : "索引分析完成；未执行任何删除。";
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await PublishTerminalAsync(run, "分析已取消；当前显示的是部分结果。").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PublishTerminalAsync(run, $"分析失败；当前结果不完整：{ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await PublishAsync(run, () => IsRunning = false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup UI finalization: {ex.Message}"); }
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    public void CancelAnalysis()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* run already finalized */ }
    }

    private async Task<bool> RunAsync(string scope, DateTime referenceTime, long run, CancellationToken token)
    {
        long cursor = 0;
        int displayed = 0, analyzedDelta = 0;
        var batch = new List<CleanupCandidateRowViewModel>(UiBatchSize);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var page = await _reader.GetIndexPageAsync(scope, cursor, PageSize, token).ConfigureAwait(false);
                if (page.Count > PageSize) throw new InvalidDataException("索引分页超出约定大小。");
                if (page.Count == 0) return false;
                foreach (var item in page)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Id <= cursor) throw new InvalidDataException("索引分页没有严格递增的 Id。");
                    cursor = item.Id;
                    var candidate = _analyzer.Analyze(item, referenceTime);
                    analyzedDelta++;
                    if (candidate is not null)
                    {
                        batch.Add(new CleanupCandidateRowViewModel(candidate));
                        displayed++;
                    }
                    if (analyzedDelta >= UiBatchSize || displayed >= _candidateLimit)
                    {
                        await FlushAsync(batch, analyzedDelta, run, token).ConfigureAwait(false);
                        batch.Clear(); analyzedDelta = 0;
                    }
                    if (displayed >= _candidateLimit) return true; // conservative partial status even at exact end
                }
                await FlushAsync(batch, analyzedDelta, run, token).ConfigureAwait(false);
                batch.Clear(); analyzedDelta = 0;
            }
        }
        finally
        {
            await FlushAsync(batch, analyzedDelta, run, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task FlushAsync(List<CleanupCandidateRowViewModel> batch, int analyzed, long run, CancellationToken token)
    {
        if (analyzed == 0) return Task.CompletedTask;
        var rows = batch.ToArray(); // worker can reuse its buffer only after this awaited publication
        return PublishAsync(run, () =>
        {
            foreach (var row in rows)
            {
                row.PropertyChanged += RowSelectionChanged;
                switch (row.Candidate.RiskLevel)
                {
                    case JunkRiskLevel.Safe: SafeCount++; break;
                    case JunkRiskLevel.Review: ReviewCount++; break;
                    case JunkRiskLevel.Protected: ProtectedCount++; break;
                }
            }
            _rows.AddBatch(rows);
            AnalyzedCount += analyzed;
            CandidateCount = _rows.Count;
            UpdateSelectionTotals();
        }, token);
    }
    private Task PublishAsync(long run, Action action, CancellationToken token)
    {
        if (_closed) return Task.CompletedTask;
        return _ui.InvokeAsync(() => { if (!_closed && run == Interlocked.Read(ref _runId)) action(); }, token);
    }
    private async Task PublishTerminalAsync(long run, string status)
    {
        try { await PublishAsync(run, () => { IsPartial = true; StatusMessage = status; }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup UI status: {ex.Message}"); }
    }
    private void ResetRows()
    {
        foreach (var row in _rows) row.PropertyChanged -= RowSelectionChanged;
        SelectedCandidate = null;
        _rows.Clear();
        AnalyzedCount = 0; CandidateCount = SafeCount = ReviewCount = ProtectedCount = 0;
        SelectedCount = 0; SelectedEstimatedBytes = 0; IsPartial = false;
    }
    private void RowSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CleanupCandidateRowViewModel.IsSelected)) UpdateSelectionTotals();
    }
    private void UpdateSelectionTotals()
    {
        int count = 0; decimal bytes = 0;
        foreach (var row in _rows)
            if (row.IsSelected && row.IsEligibleForSelection) { count++; bytes += row.Candidate.ReclaimableBytes; }
        SelectedCount = count; SelectedEstimatedBytes = bytes;
    }
    public void OnWindowClosing()
    {
        _closed = true;
        Interlocked.Increment(ref _runId);
        CancelAnalysis();
        ResetRows();
        IsRunning = false;
    }
}
