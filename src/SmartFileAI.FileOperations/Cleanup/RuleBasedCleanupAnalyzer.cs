using System.Globalization;
using System.Security;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Cleanup;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;
using SmartFileAI.FileOperations.Safety;

namespace SmartFileAI.FileOperations.Cleanup;

/// <summary>
/// Only inspects a single target; never deletes, writes, traverses recursively or authorizes deletion.
/// AI can advise. AI cannot authorize deletion. Any future executor must obtain explicit confirmation
/// and independently revalidate through FileOperationService / DeletionSafetyPolicy / Recycle Bin.
/// </summary>
public sealed class RuleBasedCleanupAnalyzer : ICleanupAnalyzer
{
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };
    // Classification only, not a second deletion allow/deny policy. These targets require Windows tooling.
    private static readonly HashSet<string> SystemMaintenanceSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.old", "SoftwareDistribution", "WinSxS", "DriverStore", "DeliveryOptimization",
        "Delivery Optimization", "$WINDOWS.~BT", "$WINDOWS.~WS", "$WinREAgent", "Rollback"
    };
    // Contextual risk, not deletion authorization. An old .tmp is insufficient to recommend app caches/user folders.
    private static readonly HashSet<string> ReviewContextSegments = new(StringComparer.OrdinalIgnoreCase)
        { "Desktop", "Downloads", "AppData", "Cache", "Caches", "BrowserCache", "WeChat", "WeChat Files", "QQ", "QQ Files", "GameCache" };
    private readonly Func<string, bool> _isFileLocked;

    public RuleBasedCleanupAnalyzer(IFileOperationService? fileOperationService = null)
        => _isFileLocked = (fileOperationService ?? new FileOperationService()).IsFileLocked;

    public CleanupCandidate? Analyze(FileItem item, DateTime referenceTime)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (referenceTime.Kind != DateTimeKind.Utc)
            throw new ArgumentException("referenceTime must be explicit UTC.", nameof(referenceTime));

        var evidence = new List<string> { $"ReferenceTimeUtc={referenceTime:O}", $"TargetPath={item.FullPath}" };
        string path;
        try
        {
            var safety = DeletionSafetyPolicy.Evaluate(item.FullPath);
            evidence.Add($"PathSafety={safety.Reason}; Permitted={safety.IsPermitted}; {safety.Details}");
            if (!safety.IsPermitted)
            {
                bool system = safety.Reason == DeletionRejectionReason.SystemProtectedDirectory;
                return Candidate(item, system ? JunkCategory.WindowsSystemCleanupCandidate : Classify(item.FullPath),
                    JunkRiskLevel.Protected, system ? CleanupRecommendation.SystemManagedCleanupOnly
                        : CleanupRecommendation.NotRecommended,
                    system ? SystemReason : $"删除安全策略拒绝该路径：{safety.Reason}。不能推荐清理。",
                    evidence, requiresElevation: system);
            }

            path = Path.GetFullPath(item.FullPath);
            if (path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(SystemMaintenanceSegments.Contains))
                return Candidate(item, JunkCategory.WindowsSystemCleanupCandidate, JunkRiskLevel.Protected,
                    CleanupRecommendation.SystemManagedCleanupOnly, SystemReason, evidence, requiresElevation: true);

            // A stale index claiming a reparse point is not sufficient evidence to upgrade to Safe.
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                evidence.Add("IndexedAttributes=ReparsePoint");
                return Candidate(item, Classify(path), JunkRiskLevel.Protected, CleanupRecommendation.NotRecommended,
                    "索引标记为重解析点；不能作为普通文件清理。", evidence);
            }

            var attributes = File.GetAttributes(path); // Throws for missing/inaccessible targets; Exists alone hides errors.
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            info.Refresh();
            evidence.Add($"Exists={info.Exists}; Attributes={attributes}; LinkTarget={info.LinkTarget ?? "none"}");
            if (!info.Exists) throw new FileNotFoundException("Target disappeared during analysis.", path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget != null)
                return Candidate(item, Classify(path), JunkRiskLevel.Protected, CleanupRecommendation.NotRecommended,
                    "实时目标是链接或重解析点，不能清理。", evidence);
            if (item.IsDirectory != isDirectory)
                return Candidate(item, Classify(path), JunkRiskLevel.Review, CleanupRecommendation.InsufficientEvidence,
                    "索引与磁盘对象类型不同，必须重新扫描。", evidence);

            if (isDirectory)
            {
                // MoveNext is inside this try too: enumeration-time failures cannot become an empty-directory result.
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                if (entries.MoveNext()) return null;
                evidence.Add("DirectoryExists=True; EmptyDirectory=True; ReclaimableBytes=0");
                var finalSafety = DeletionSafetyPolicy.Evaluate(path);
                evidence.Add($"FinalPathSafety={finalSafety.Reason}; Permitted={finalSafety.IsPermitted}");
                if (!finalSafety.IsPermitted)
                    return Candidate(item, JunkCategory.EmptyDirectory, JunkRiskLevel.Protected,
                        CleanupRecommendation.NotRecommended, "空目录检查后安全策略拒绝该路径。", evidence);
                return Candidate(item, JunkCategory.EmptyDirectory, JunkRiskLevel.Review,
                    CleanupRecommendation.NeedsReview, "目录当前为空，但可能承担应用或用户组织用途，需要人工确认。", evidence);
            }

            var category = Classify(path);
            if (category == JunkCategory.Unknown) return null;

            var file = (FileInfo)info;
            DateTime modified = file.LastWriteTimeUtc;
            TimeSpan age = referenceTime - modified;
            long bytes = file.Length;
            evidence.Add($"Extension={file.Extension.ToLowerInvariant()}");
            evidence.Add($"ModifiedTimeUtc={modified:O}; IndexedModifiedTime={item.ModifiedTime:O}");
            evidence.Add($"AgeDays={age.TotalDays.ToString("R", CultureInfo.InvariantCulture)}; SizeBytes={bytes}");
            bool locked = _isFileLocked(path);
            evidence.Add($"LockState={(locked ? "Locked" : "HeuristicNotLocked")}");
            if (locked)
                return Candidate(item, category, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview,
                    "文件当前被锁定或使用中，不能推荐清理。", evidence, bytes);

            // The existing heuristic returns false on access denied. Positive read-only exclusive-open evidence
            // closes that uncertainty without changing the deletion service or duplicating its lock algorithm.
            using var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            evidence.Add("LockState=ExclusiveReadOpenConfirmed (point-in-time only)");
            file.Refresh();
            var finalCheck = DeletionSafetyPolicy.Evaluate(path);
            evidence.Add($"FinalPathSafety={finalCheck.Reason}; Permitted={finalCheck.IsPermitted}");
            if (!finalCheck.IsPermitted || !file.Exists || file.LastWriteTimeUtc != modified || file.Length != bytes
                || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget != null)
                return Candidate(item, category, JunkRiskLevel.Review, CleanupRecommendation.InsufficientEvidence,
                    "分析期间目标或安全证据变化，不能推荐清理。", evidence);

            if (category == JunkCategory.OldLog)
                return age >= TimeSpan.FromDays(30)
                    ? Candidate(item, category, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview,
                        "日志至少 30 天未修改，仍可能有诊断或审计价值，必须人工复核。", evidence, bytes)
                    : null;

            int minimumDays = category == JunkCategory.OfficeTemporaryFile ? 1 : 7;
            if (modified <= DateTime.FromFileTimeUtc(0) || age < TimeSpan.Zero)
                return Candidate(item, category, JunkRiskLevel.Review, CleanupRecommendation.InsufficientEvidence,
                    "最后修改时间无效或晚于参考时间，年龄证据不足。", evidence, bytes);
            if (age < TimeSpan.FromDays(minimumDays))
                return Candidate(item, category, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview,
                    category == JunkCategory.CrashDump ? "近期转储可能仍用于问题诊断，需要人工复核。"
                        : $"文件未达到 {minimumDays} 天年龄门槛，不能推荐清理。", evidence, bytes);

            if (HasReviewContext(path))
            {
                evidence.Add("ContextSafety=UserFolderOrApplicationCache; automatic recommendation prohibited");
                return Candidate(item, category, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview,
                    "路径属于用户存储或应用缓存语境，仅凭扩展名和年龄不足以推荐清理。", evidence, bytes);
            }

            return Candidate(item, category, JunkRiskLevel.Safe, CleanupRecommendation.Recommended,
                $"文件至少 {minimumDays} 天未修改，实时存在、非目录、非链接、路径策略允许且当前独占只读打开成功；可作为清理建议。",
                evidence, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            evidence.Add($"InspectionFailure={ex.GetType().Name}: {ex.Message}; LockState=Unconfirmed");
            return Candidate(item, item.IsDirectory ? JunkCategory.EmptyDirectory : Classify(item.FullPath),
                JunkRiskLevel.Review, CleanupRecommendation.InsufficientEvidence,
                "无法确认存在性、属性、年龄、空目录状态或锁状态；不能推荐清理。", evidence,
                requiresElevation: ex is UnauthorizedAccessException or SecurityException);
        }
    }

    private const string SystemReason = "需要 Windows 官方支持的系统清理机制；不能使用普通 File.Delete / Directory.Delete 清理。";

    private static bool HasReviewContext(string path)
    {
        // Windows temp commonly lives beneath AppData. Inspect its relative suffix, allowing normal temp files
        // while still treating any nested AppData/cache/user-folder context conservatively.
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string contextPath = path.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(tempRoot, path) : path;
        return contextPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(ReviewContextSegments.Contains);
    }

    private static JunkCategory Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return JunkCategory.Unknown;
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(name);
        if (name.StartsWith("~$", StringComparison.Ordinal) && OfficeExtensions.Contains(extension))
            return JunkCategory.OfficeTemporaryFile;
        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".temp", StringComparison.OrdinalIgnoreCase)) return JunkCategory.TemporaryFile;
        if (extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase)) return JunkCategory.CrashDump;
        if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase)) return JunkCategory.OldLog;
        return JunkCategory.Unknown;
    }

    private static CleanupCandidate Candidate(FileItem item, JunkCategory category, JunkRiskLevel risk,
        CleanupRecommendation action, string reason, IEnumerable<string> evidence, long bytes = 0,
        bool requiresElevation = false)
        => new(item, category, risk, action, reason, evidence, bytes, requiresElevation);
}
