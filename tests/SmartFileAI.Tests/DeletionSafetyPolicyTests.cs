using System;
using System.IO;
using SmartFileAI.FileOperations.Safety;
using Xunit;

namespace SmartFileAI.Tests;

public sealed class DeletionSafetyPolicyTests : IDisposable
{
    private readonly string _testRoot;

    public DeletionSafetyPolicyTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SmartFileAI_SafetyTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_NullOrWhitespace_ShouldDeny(string? path)
    {
        var result = DeletionSafetyPolicy.Evaluate(path);
        Assert.False(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.EmptyOrWhitespacePath, result.Reason);
    }

    [Theory]
    [InlineData(@"..\test.txt")]
    [InlineData(@".\abc")]
    [InlineData(@"relative/file.txt")]
    public void Evaluate_RelativePath_ShouldDeny(string path)
    {
        var result = DeletionSafetyPolicy.Evaluate(path);
        Assert.False(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.NotRootedOrRelativePath, result.Reason);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"c:\")]
    [InlineData(@"D:\")]
    [InlineData(@"E:\")]
    public void Evaluate_DriveRootDirectory_ShouldDeny(string driveRoot)
    {
        var result = DeletionSafetyPolicy.Evaluate(driveRoot);
        Assert.False(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.DriveRootDirectory, result.Reason);
    }

    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"\\192.168.1.1\c$\secret")]
    [InlineData(@"//server/share/file.txt")]
    public void Evaluate_UncNetworkPath_ShouldDeny(string uncPath)
    {
        var result = DeletionSafetyPolicy.Evaluate(uncPath);
        Assert.False(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.UncPath, result.Reason);
    }

    [Theory]
    [InlineData(@"\\?\C:\test.txt")]
    [InlineData(@"\\.\C:\test.txt")]
    [InlineData(@"//?/C:/test.txt")]
    [InlineData(@"//./C:/test.txt")]
    public void Evaluate_ExtendedDevicePath_ShouldDeny(string devicePath)
    {
        var result = DeletionSafetyPolicy.Evaluate(devicePath);
        Assert.False(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.ExtendedDevicePath, result.Reason);
    }

    [Theory]
    [InlineData(@"C:\temp\a.txt:stream")]
    [InlineData(@"C:\temp\a.txt:$DATA")]
    [InlineData(@"D:\data.bin:zone.identifier")]
    public void Evaluate_AlternateDataStream_ShouldDeny(string adsPath)
    {
        var result = DeletionSafetyPolicy.Evaluate(adsPath);
        Assert.False(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.AlternateDataStream, result.Reason);
    }

    [Fact]
    public void Evaluate_SystemProtectedDirectories_ShouldDeny()
    {
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(winDir))
        {
            var resWin = DeletionSafetyPolicy.Evaluate(winDir);
            Assert.False(resWin.IsPermitted);
            Assert.Equal(DeletionRejectionReason.SystemProtectedDirectory, resWin.Reason);

            string sysFile = Path.Combine(winDir, "System32", "notepad.exe");
            var resFile = DeletionSafetyPolicy.Evaluate(sysFile);
            Assert.False(resFile.IsPermitted);
            Assert.Equal(DeletionRejectionReason.SystemProtectedDirectory, resFile.Reason);
        }

        var resRecycle = DeletionSafetyPolicy.Evaluate(@"C:\$Recycle.Bin");
        Assert.False(resRecycle.IsPermitted);
        Assert.Equal(DeletionRejectionReason.SystemProtectedDirectory, resRecycle.Reason);

        var resVolumeInfo = DeletionSafetyPolicy.Evaluate(@"C:\System Volume Information");
        Assert.False(resVolumeInfo.IsPermitted);
        Assert.Equal(DeletionRejectionReason.SystemProtectedDirectory, resVolumeInfo.Reason);
    }

    [Fact]
    public void Evaluate_OrdinaryTempFile_ShouldAllow()
    {
        string tempFile = Path.Combine(_testRoot, "normal_file.txt");
        File.WriteAllText(tempFile, "hello");

        var result = DeletionSafetyPolicy.Evaluate(tempFile);
        Assert.True(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.None, result.Reason);
    }

    [Fact]
    public void Evaluate_OrdinaryTempDirectory_ShouldAllow()
    {
        string tempSubDir = Path.Combine(_testRoot, "normal_sub_dir");
        Directory.CreateDirectory(tempSubDir);

        var result = DeletionSafetyPolicy.Evaluate(tempSubDir);
        Assert.True(result.IsPermitted);
        Assert.Equal(DeletionRejectionReason.None, result.Reason);
    }

    [Fact]
    public void Evaluate_RealSymbolicLinkTarget_ShouldDeny()
    {
        string realTarget = Path.Combine(_testRoot, "RealTargetA");
        Directory.CreateDirectory(realTarget);
        string linkDir = Path.Combine(_testRoot, "LinkDirectoryA");

        try
        {
            Directory.CreateSymbolicLink(linkDir, realTarget);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Strictly enforce failure to prevent a False Green in any xUnit v2 environment.
            Assert.True(false, $"SymbolicLink creation not available: {ex.GetType().Name}: {ex.Message}. This test requires Developer Mode or elevated privileges on Windows. Failing test to prevent false green.");
        }

        try
        {
            var result = DeletionSafetyPolicy.Evaluate(linkDir);
            Assert.False(result.IsPermitted);
            Assert.Equal(DeletionRejectionReason.ReparsePointOrLinkDetected, result.Reason);
        }
        finally
        {
            try
            {
                if (Directory.Exists(linkDir))
                    Directory.Delete(linkDir, recursive: false);
            }
            catch { }
        }
    }

    [Fact]
    public void Evaluate_RealSymbolicLinkAncestor_ShouldDenyChildDeletion()
    {
        string realTarget = Path.Combine(_testRoot, "RealTargetB");
        Directory.CreateDirectory(realTarget);
        string realFile = Path.Combine(realTarget, "file.txt");
        File.WriteAllText(realFile, "protected content");

        string linkDir = Path.Combine(_testRoot, "LinkDirectoryB");

        try
        {
            Directory.CreateSymbolicLink(linkDir, realTarget);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.True(false, $"SymbolicLink creation not available: {ex.GetType().Name}: {ex.Message}. This test requires Developer Mode or elevated privileges on Windows. Failing test to prevent false green.");
        }

        string linkPathToFile = Path.Combine(linkDir, "file.txt");

        try
        {
            var result = DeletionSafetyPolicy.Evaluate(linkPathToFile);
            Assert.False(result.IsPermitted);
            Assert.Equal(DeletionRejectionReason.ReparsePointOrLinkDetected, result.Reason);
        }
        finally
        {
            try
            {
                if (Directory.Exists(linkDir))
                    Directory.Delete(linkDir, recursive: false);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }
}