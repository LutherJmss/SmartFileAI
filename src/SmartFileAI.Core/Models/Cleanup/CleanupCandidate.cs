using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;

namespace SmartFileAI.Core.Models.Cleanup;

/// <summary>An analysis snapshot, never deletion authorization. AI can advise; AI cannot authorize deletion.</summary>
public sealed class CleanupCandidate
{
    private bool _isSelected;

    public FileItem Item { get; }
    public JunkCategory Category { get; }
    public JunkRiskLevel RiskLevel { get; }
    public CleanupRecommendation RecommendedAction { get; }
    public string Reason { get; }
    public IReadOnlyList<string> Evidence { get; }
    public long ReclaimableBytes { get; }
    public bool RequiresElevation { get; }
    // False unless a future executor can actually establish reversibility for this target.
    public bool Reversible { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && RiskLevel == JunkRiskLevel.Protected)
                throw new InvalidOperationException("Protected cleanup candidates cannot be selected.");
            _isSelected = value;
        }
    }

    public CleanupCandidate(FileItem item, JunkCategory category, JunkRiskLevel riskLevel,
        CleanupRecommendation recommendedAction, string reason, IEnumerable<string> evidence,
        long reclaimableBytes, bool requiresElevation = false, bool reversible = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!Enum.IsDefined(category) || !Enum.IsDefined(riskLevel) || !Enum.IsDefined(recommendedAction))
            throw new ArgumentOutOfRangeException(nameof(riskLevel), "Cleanup classification must be defined.");
        if (reclaimableBytes < 0) throw new ArgumentOutOfRangeException(nameof(reclaimableBytes));
        if (recommendedAction == CleanupRecommendation.Recommended && riskLevel != JunkRiskLevel.Safe)
            throw new ArgumentException("Only Safe candidates may be Recommended.", nameof(recommendedAction));
        var entries = evidence.ToArray();
        if (entries.Length == 0 || entries.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Non-empty evidence is required.", nameof(evidence));

        Item = item;
        Category = category;
        RiskLevel = riskLevel;
        RecommendedAction = recommendedAction;
        Reason = reason;
        Evidence = Array.AsReadOnly(entries);
        ReclaimableBytes = reclaimableBytes;
        RequiresElevation = requiresElevation;
        Reversible = reversible;
        _isSelected = riskLevel == JunkRiskLevel.Safe && recommendedAction == CleanupRecommendation.Recommended;
    }
}
