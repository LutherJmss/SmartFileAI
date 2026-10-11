using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileAI.Core.Models.Cleanup;
using SmartFileAI.Core.Models.Enums;

namespace SmartFileAI.UI.ViewModels;

public sealed class CleanupCandidateRowViewModel : ObservableObject
{
    private bool _isSelected;
    public CleanupCandidate Candidate { get; }
    public CleanupCandidateRowViewModel(CleanupCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
        _isSelected = IsEligibleForSelection && candidate.IsSelected;
        if (!IsEligibleForSelection) candidate.IsSelected = false;
    }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !IsEligibleForSelection) return;
            if (SetProperty(ref _isSelected, value)) Candidate.IsSelected = value;
        }
    }
    public bool IsEligibleForSelection => Candidate.RiskLevel == JunkRiskLevel.Safe
        && Candidate.RecommendedAction == CleanupRecommendation.Recommended;
    public string FullPath => Candidate.Item.FullPath;
    public string FileName => Candidate.Item.Name;
    public string Extension => Candidate.Item.IsDirectory ? "文件夹" : Candidate.Item.Extension;
    public long Size => Candidate.Item.Size;
    public DateTime ModifiedTime => Candidate.Item.ModifiedTime;
    public string Reason => Candidate.Reason;
    public IReadOnlyList<string> Evidence => Candidate.Evidence;
    public string ElevationExplanation => Candidate.RequiresElevation
        ? "可能需要管理员权限或 Windows 官方清理机制；权限提升不能绕过保护规则。" : "分析不需要主动提升权限。";
    public string ReversibilityExplanation => Candidate.Reversible
        ? "候选标记可能支持恢复；本窗口不执行操作。" : "恢复能力尚未确认；本窗口不执行删除。";
    public string Category => Candidate.Category switch
    {
        JunkCategory.TemporaryFile => "临时文件", JunkCategory.OfficeTemporaryFile => "Office 临时文件",
        JunkCategory.CrashDump => "崩溃转储", JunkCategory.OldLog => "旧日志", JunkCategory.EmptyDirectory => "空目录",
        JunkCategory.WindowsSystemCleanupCandidate => "Windows 系统维护候选", _ => "未知"
    };
    public string RiskLevel => Candidate.RiskLevel switch
        { JunkRiskLevel.Safe => "安全建议", JunkRiskLevel.Review => "需要复核", _ => "受保护" };
    public string Recommendation => Candidate.RecommendedAction switch
    {
        CleanupRecommendation.Recommended => "建议", CleanupRecommendation.NeedsReview => "人工复核",
        CleanupRecommendation.InsufficientEvidence => "证据不足", CleanupRecommendation.SystemManagedCleanupOnly => "仅系统官方清理",
        _ => "不建议"
    };
}
