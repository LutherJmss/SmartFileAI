using System.Diagnostics;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Cleanup;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;
using SmartFileAI.FileOperations;
using SmartFileAI.FileOperations.Cleanup;
using Xunit;

namespace SmartFileAI.Tests;

public sealed class RuleBasedCleanupAnalyzerTests : IDisposable
{
    private static readonly DateTime Reference = new(2025, 2, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmartFileAI_Cleanup_" + Guid.NewGuid().ToString("N"));
    private readonly RuleBasedCleanupAnalyzer _analyzer = new();
    private readonly List<(string Path, bool Directory)> _links = new();

    public RuleBasedCleanupAnalyzerTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("old.tmp")]
    [InlineData("old.temp")]
    [InlineData("old.TMP")]
    public void OldTemporaryFile_IsSafeRecommendedWithActualBytes(string name)
    {
        var item = CreateFile(name, TimeSpan.FromDays(8));
        item.Size = 99999; // The engine must use live evidence, not stale indexed size.
        var candidate = Analyze(item);
        AssertCandidate(candidate, JunkCategory.TemporaryFile, JunkRiskLevel.Safe, CleanupRecommendation.Recommended);
        Assert.True(candidate.IsSelected);
        Assert.Equal(7, candidate.ReclaimableBytes);
        Assert.False(candidate.Reversible); // Analysis cannot promise recycle-bin support.
        Assert.False(candidate.RequiresElevation);
    }

    [Fact]
    public void SafeCandidateEvidence_ExplainsEverySafetyGate()
    {
        var candidate = Analyze(CreateFile("evidence.tmp", TimeSpan.FromDays(8)));
        AssertCandidate(candidate, JunkCategory.TemporaryFile, JunkRiskLevel.Safe, CleanupRecommendation.Recommended);
        Assert.Contains(candidate.Evidence, entry => entry == "Extension=.tmp");
        Assert.Contains(candidate.Evidence, entry => entry.Contains("ModifiedTimeUtc=", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, entry => entry.Contains("AgeDays=8; SizeBytes=7", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, entry => entry.StartsWith("PathSafety=None; Permitted=True", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, entry => entry.Contains("Exists=True", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, entry => entry.Contains("LinkTarget=none", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, entry => entry.Contains("ExclusiveReadOpenConfirmed", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, entry => entry.StartsWith("FinalPathSafety=None; Permitted=True", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(167, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview)] // 6d23h
    [InlineData(168, JunkRiskLevel.Safe, CleanupRecommendation.Recommended)] // exactly 7d
    [InlineData(169, JunkRiskLevel.Safe, CleanupRecommendation.Recommended)] // 7d+
    public void TemporaryAgeBoundary_IsInclusiveAndDeterministic(int hours, JunkRiskLevel risk, CleanupRecommendation action)
    {
        var item = CreateFile("boundary.tmp", TimeSpan.FromHours(hours));
        AssertCandidate(Analyze(item), JunkCategory.TemporaryFile, risk, action);
    }

    [Theory]
    [InlineData("~$report.doc")]
    [InlineData("~$report.docx")]
    [InlineData("~$report.xls")]
    [InlineData("~$report.xlsx")]
    [InlineData("~$report.ppt")]
    [InlineData("~$report.pptx")]
    public void OldOfficeTemporaryFile_IsSafe(string name)
        => AssertCandidate(Analyze(CreateFile(name, TimeSpan.FromDays(1))), JunkCategory.OfficeTemporaryFile,
            JunkRiskLevel.Safe, CleanupRecommendation.Recommended);

    [Fact]
    public void RecentOfficeTemporaryFile_IsReviewUnselected()
    {
        var candidate = Analyze(CreateFile("~$recent.docx", TimeSpan.FromHours(23)));
        AssertCandidate(candidate, JunkCategory.OfficeTemporaryFile, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview);
        Assert.False(candidate.IsSelected);
    }

    [Fact]
    public void LockedOfficeTemporaryFile_IsNeverSafe()
    {
        var item = CreateFile("~$locked.docx", TimeSpan.FromDays(2));
        using var handle = File.Open(item.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var candidate = Analyze(item);
        AssertCandidate(candidate, JunkCategory.OfficeTemporaryFile, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview);
        Assert.False(candidate.IsSelected);
        Assert.Contains(candidate.Evidence, entry => entry.Contains("LockState=Locked", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(167, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview)]
    [InlineData(168, JunkRiskLevel.Safe, CleanupRecommendation.Recommended)]
    [InlineData(169, JunkRiskLevel.Safe, CleanupRecommendation.Recommended)]
    public void CrashDumpAgeBoundary_PreservesRecentDiagnosticData(int hours, JunkRiskLevel risk, CleanupRecommendation action)
        => AssertCandidate(Analyze(CreateFile("crash.dmp", TimeSpan.FromHours(hours))), JunkCategory.CrashDump, risk, action);

    [Fact]
    public void OldLog_IsAlwaysReviewAndUnselected()
    {
        var candidate = Analyze(CreateFile("diagnostic.log", TimeSpan.FromDays(30)));
        AssertCandidate(candidate, JunkCategory.OldLog, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview);
        Assert.False(candidate.IsSelected);
    }

    [Fact]
    public void LogBelowThirtyDays_HasNoOldLogCandidate()
        => Assert.Null(_analyzer.Analyze(CreateFile("recent.log", TimeSpan.FromHours(719)), Reference));

    [Fact]
    public void EmptyDirectory_IsReviewWithZeroBytes()
    {
        var item = CreateDirectory("empty");
        var candidate = Analyze(item);
        AssertCandidate(candidate, JunkCategory.EmptyDirectory, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview);
        Assert.False(candidate.IsSelected);
        Assert.Equal(0, candidate.ReclaimableBytes);
        Assert.Contains(candidate.Evidence, entry => entry.Contains("EmptyDirectory=True", StringComparison.Ordinal));
    }

    [Fact]
    public void NonEmptyDirectory_WithChildDirectory_IsNotAnEmptyCandidate()
    {
        var item = CreateDirectory("parent");
        Directory.CreateDirectory(Path.Combine(item.FullPath, "child"));
        Assert.Null(_analyzer.Analyze(item, Reference));
    }

    [Fact]
    public void NonEmptyDirectory_WithFile_IsNotAnEmptyCandidate()
    {
        var item = CreateDirectory("parent");
        File.WriteAllText(Path.Combine(item.FullPath, "child.txt"), "content");
        Assert.Null(_analyzer.Analyze(item, Reference));
    }

    [Fact]
    public void FileSymbolicLink_IsProtectedAndLeavesTargetIntact()
    {
        var target = CreateFile("target.tmp", TimeSpan.FromDays(8));
        string link = Path.Combine(_root, "linked.tmp");
        CreateLink(link, target.FullPath, directory: false);
        var candidate = Analyze(new FileItem { FullPath = link });
        Assert.Equal(JunkRiskLevel.Protected, candidate.RiskLevel);
        Assert.False(candidate.IsSelected);
        Assert.Equal("payload", File.ReadAllText(target.FullPath));
    }

    [Fact]
    public void DirectorySymbolicLink_IsProtected_NotAnEmptyDirectory()
    {
        var target = CreateDirectory("target");
        string link = Path.Combine(_root, "linked");
        CreateLink(link, target.FullPath, directory: true);
        var candidate = Analyze(new FileItem { FullPath = link, IsDirectory = true });
        Assert.Equal(JunkRiskLevel.Protected, candidate.RiskLevel);
        Assert.NotEqual(CleanupRecommendation.Recommended, candidate.RecommendedAction);
        Assert.True(Directory.Exists(target.FullPath));
    }

    [Fact]
    public void SymbolicLinkAncestor_PreventsSafeChildCandidate()
    {
        var dir = CreateDirectory("target");
        string child = Path.Combine(dir.FullPath, "child.tmp");
        File.WriteAllText(child, "payload");
        File.SetLastWriteTimeUtc(child, Reference.AddDays(-8));
        string link = Path.Combine(_root, "linked");
        CreateLink(link, dir.FullPath, directory: true);
        var candidate = Analyze(new FileItem { FullPath = Path.Combine(link, "child.tmp") });
        Assert.Equal(JunkRiskLevel.Protected, candidate.RiskLevel);
        Assert.False(candidate.IsSelected);
        Assert.Equal("payload", File.ReadAllText(child));
    }

    [Fact]
    public async Task WindowsJunction_PreventsSafeChildCandidate()
    {
        Assert.True(OperatingSystem.IsWindows(), "Junction safety test requires Windows; it must not be silently skipped.");
        var target = CreateDirectory("junctionTarget");
        string child = Path.Combine(target.FullPath, "child.tmp");
        File.WriteAllText(child, "payload");
        File.SetLastWriteTimeUtc(child, Reference.AddDays(-8));
        string junction = Path.Combine(_root, "junction");
        _links.Add((junction, true));
        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{target.FullPath}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch junction creation.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(10000);
        if (!exited) process.Kill(entireProcessTree: true);
        Assert.True(exited, "Junction creation timed out.");
        string output = await outputTask + await errorTask;
        Assert.True(process.ExitCode == 0 && Directory.Exists(junction),
            $"Junction creation failed: {output}. Requires Windows Developer Mode or elevated privileges where applicable; no false green.");
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        var candidate = Analyze(new FileItem { FullPath = Path.Combine(junction, "child.tmp") });
        Assert.Equal(JunkRiskLevel.Protected, candidate.RiskLevel);
        Assert.Equal("payload", File.ReadAllText(child));
    }

    [Fact]
    public void IndexedReparseAttribute_CannotBeUpgradedToSafe()
    {
        var item = CreateFile("indexed.tmp", TimeSpan.FromDays(8));
        item.Attributes |= FileAttributes.ReparsePoint;
        Assert.Equal(JunkRiskLevel.Protected, Analyze(item).RiskLevel);
    }

    [Fact]
    public void ProtectedPath_UsesExistingPolicyWithoutAccessingSystemFiles()
    {
        var dir = CreateDirectory("$Recycle.Bin");
        var candidate = Analyze(dir);
        AssertCandidate(candidate, JunkCategory.WindowsSystemCleanupCandidate, JunkRiskLevel.Protected,
            CleanupRecommendation.SystemManagedCleanupOnly);
        Assert.True(candidate.RequiresElevation);
        Assert.False(candidate.IsSelected);
    }

    [Theory]
    [InlineData("Windows.old")]
    [InlineData("SoftwareDistribution")]
    [InlineData("WinSxS")]
    [InlineData("DriverStore")]
    [InlineData("DeliveryOptimization")]
    [InlineData("$WINDOWS.~BT")]
    public void SystemMaintenancePath_IsSystemManagedOnly(string name)
    {
        var candidate = Analyze(CreateDirectory(name));
        AssertCandidate(candidate, JunkCategory.WindowsSystemCleanupCandidate, JunkRiskLevel.Protected,
            CleanupRecommendation.SystemManagedCleanupOnly);
        Assert.Contains("Windows", candidate.Reason);
        Assert.False(candidate.IsSelected);
    }

    [Fact]
    public void ReviewCanOnlyBecomeSelectedExplicitly_ProtectedCanNeverBeSelected()
    {
        var review = Analyze(CreateFile("recent.dmp", TimeSpan.FromHours(1)));
        Assert.False(review.IsSelected);
        review.IsSelected = true;
        Assert.True(review.IsSelected);
        var protectedCandidate = Analyze(CreateDirectory("Windows.old"));
        Assert.Throws<InvalidOperationException>(() => protectedCandidate.IsSelected = true);
        Assert.False(protectedCandidate.IsSelected);
    }

    [Theory]
    [InlineData("installer.exe")]
    [InlineData("installer.msi")]
    [InlineData("archive.zip")]
    [InlineData("archive.rar")]
    [InlineData("unknown.bin")]
    [InlineData("ordinary.docx")]
    public void UnknownFiles_AreNeverAutomaticallyRecommended(string name)
        => Assert.Null(_analyzer.Analyze(CreateFile(name, TimeSpan.FromDays(100)), Reference));

    [Fact]
    public void OrdinaryZeroByteFile_IsNotRecommended()
    {
        var item = CreateFile("zero.bin", TimeSpan.FromDays(100));
        File.WriteAllBytes(item.FullPath, Array.Empty<byte>());
        Assert.Null(_analyzer.Analyze(item, Reference));
    }

    [Theory]
    [InlineData("Desktop")]
    [InlineData("Downloads")]
    [InlineData("Cache")]
    [InlineData("AppData")]
    [InlineData("WeChat Files")]
    [InlineData("QQ Files")]
    [InlineData("GameCache")]
    public void OldTmpInUserOrCacheContext_IsNotSafe(string folder)
    {
        var directory = CreateDirectory(folder);
        string path = Path.Combine(directory.FullPath, "old.tmp");
        File.WriteAllText(path, "payload");
        File.SetLastWriteTimeUtc(path, Reference.AddDays(-8));
        var candidate = Analyze(new FileItem { FullPath = path });
        AssertCandidate(candidate, JunkCategory.TemporaryFile, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview);
        Assert.Contains(candidate.Evidence, entry => entry.Contains("ContextSafety=", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingFile_IsInsufficientEvidenceNeverSafe()
    {
        var candidate = Analyze(new FileItem { FullPath = Path.Combine(_root, "missing.tmp"), ModifiedTime = Reference.AddDays(-8) });
        AssertCandidate(candidate, JunkCategory.TemporaryFile, JunkRiskLevel.Review, CleanupRecommendation.InsufficientEvidence);
        Assert.Equal(0, candidate.ReclaimableBytes);
    }

    [Fact]
    public void LiveModifiedTime_OverridesOldIndexAndForgedExtension()
    {
        var item = CreateFile("recent.tmp", TimeSpan.FromDays(1));
        item.ModifiedTime = Reference.AddDays(-100);
        item.Extension = ".dmp";
        AssertCandidate(Analyze(item), JunkCategory.TemporaryFile, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview);
    }

    [Fact]
    public void DirectoryNamedTmp_IsReviewNotSafeFile()
    {
        var candidate = Analyze(CreateDirectory("directory.tmp"));
        AssertCandidate(candidate, JunkCategory.EmptyDirectory, JunkRiskLevel.Review, CleanupRecommendation.NeedsReview);
    }

    [Fact]
    public void FutureTimestamp_IsInsufficientEvidence()
        => AssertCandidate(Analyze(CreateFile("future.tmp", TimeSpan.FromHours(-1))), JunkCategory.TemporaryFile,
            JunkRiskLevel.Review, CleanupRecommendation.InsufficientEvidence);

    [Fact]
    public void TypeMismatch_IsInsufficientEvidence()
    {
        var item = CreateFile("changed.tmp", TimeSpan.FromDays(8));
        item.IsDirectory = true;
        Assert.Equal(CleanupRecommendation.InsufficientEvidence, Analyze(item).RecommendedAction);
    }

    [Fact]
    public void UnconfirmedLockInspection_FailsClosed()
    {
        var item = CreateFile("unknown-lock.tmp", TimeSpan.FromDays(8));
        var service = new LockProbeSpy { ThrowOnProbe = true };
        var analyzer = new RuleBasedCleanupAnalyzer(service);
        var candidate = analyzer.Analyze(item, Reference);
        Assert.NotNull(candidate);
        AssertCandidate(candidate, JunkCategory.TemporaryFile, JunkRiskLevel.Review, CleanupRecommendation.InsufficientEvidence);
        Assert.Equal(1, service.ProbeCalls);
        Assert.Equal(0, service.DeleteCalls);
    }

    [Fact]
    public void AnalysisDoesNotDeleteOrAlterFiles_AndNeverCallsDeleteService()
    {
        var item = CreateFile("retained.tmp", TimeSpan.FromDays(8));
        var empty = CreateDirectory("retainedDirectory");
        var service = new LockProbeSpy();
        var analyzer = new RuleBasedCleanupAnalyzer(service);
        DateTime modified = File.GetLastWriteTimeUtc(item.FullPath);
        FileAttributes attributes = File.GetAttributes(item.FullPath);
        Assert.Equal(JunkRiskLevel.Safe, analyzer.Analyze(item, Reference)!.RiskLevel);
        Assert.Equal(JunkCategory.EmptyDirectory, analyzer.Analyze(empty, Reference)!.Category);
        Assert.Equal(0, service.DeleteCalls);
        Assert.Equal(1, service.ProbeCalls);
        Assert.True(File.Exists(item.FullPath));
        Assert.True(Directory.Exists(empty.FullPath));
        Assert.Equal("payload", File.ReadAllText(item.FullPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(item.FullPath));
        Assert.Equal(attributes, File.GetAttributes(item.FullPath));
    }

    [Fact]
    public void CandidateCopiesEvidence_AndRejectsInvalidRecommendation()
    {
        var item = CreateFile("model.tmp", TimeSpan.FromDays(8));
        var evidence = new List<string> { "observed" };
        var candidate = new CleanupCandidate(item, JunkCategory.TemporaryFile, JunkRiskLevel.Safe,
            CleanupRecommendation.Recommended, "reason", evidence, 7);
        evidence.Clear();
        Assert.Equal(new[] { "observed" }, candidate.Evidence);
        Assert.Throws<ArgumentException>(() => new CleanupCandidate(item, JunkCategory.TemporaryFile,
            JunkRiskLevel.Protected, CleanupRecommendation.Recommended, "reason", new[] { "evidence" }, 7));
        Assert.Throws<ArgumentException>(() => new CleanupCandidate(item, JunkCategory.TemporaryFile,
            JunkRiskLevel.Review, CleanupRecommendation.NeedsReview, "reason", Array.Empty<string>(), 7));
    }

    [Fact]
    public void ReferenceTimeMustBeExplicitUtc()
        => Assert.Throws<ArgumentException>(() => _analyzer.Analyze(CreateFile("clock.tmp", TimeSpan.FromDays(8)),
            DateTime.SpecifyKind(Reference, DateTimeKind.Unspecified)));

    private CleanupCandidate Analyze(FileItem item)
    {
        var candidate = _analyzer.Analyze(item, Reference);
        Assert.NotNull(candidate);
        Assert.False(string.IsNullOrWhiteSpace(candidate.Reason));
        Assert.NotEmpty(candidate.Evidence);
        Assert.All(candidate.Evidence, entry => Assert.False(string.IsNullOrWhiteSpace(entry)));
        return candidate;
    }

    private static void AssertCandidate(CleanupCandidate candidate, JunkCategory category, JunkRiskLevel risk, CleanupRecommendation action)
    {
        Assert.Equal(category, candidate.Category);
        Assert.Equal(risk, candidate.RiskLevel);
        Assert.Equal(action, candidate.RecommendedAction);
        Assert.Equal(risk == JunkRiskLevel.Safe && action == CleanupRecommendation.Recommended, candidate.IsSelected);
    }

    private FileItem CreateFile(string name, TimeSpan age)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "payload");
        File.SetLastWriteTimeUtc(path, Reference - age);
        // Verify filesystem timestamp precision; boundary cases must not silently drift.
        Assert.Equal(Reference - age, File.GetLastWriteTimeUtc(path));
        return new FileItem
        {
            Name = name, FullPath = path, ParentPath = _root, Extension = Path.GetExtension(name),
            Size = 7, ModifiedTime = Reference - age, Attributes = File.GetAttributes(path)
        };
    }

    private FileItem CreateDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return new FileItem { Name = name, FullPath = path, ParentPath = _root, IsDirectory = true, Attributes = FileAttributes.Directory };
    }

    private void CreateLink(string path, string target, bool directory)
    {
        _links.Add((path, directory));
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.True(false, $"Symbolic-link creation failed: {ex.Message}. Enable Windows Developer Mode or use elevated privileges; this safety test must not be skipped.");
        }
        Assert.True((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
    }

    public void Dispose()
    {
        // Unlink explicitly before sandbox recursion, so cleanup never follows a test link.
        foreach (var link in _links)
        {
            if (link.Directory && Directory.Exists(link.Path)) Directory.Delete(link.Path, recursive: false);
            else if (!link.Directory && File.Exists(link.Path)) File.Delete(link.Path);
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class LockProbeSpy : IFileOperationService
    {
        private readonly FileOperationService _real = new();
        public bool ThrowOnProbe { get; init; }
        public int ProbeCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool IsFileLocked(string path)
        {
            ProbeCalls++;
            if (ThrowOnProbe) throw new UnauthorizedAccessException("Lock state cannot be established.");
            return _real.IsFileLocked(path);
        }
        public Task<OperationResultType> DeleteAsync(string fullPath, FileOperationType operationType)
        {
            DeleteCalls++;
            throw new InvalidOperationException("Cleanup analysis must never invoke deletion.");
        }
    }
}
