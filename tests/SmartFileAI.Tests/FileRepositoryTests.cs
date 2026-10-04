using System.Diagnostics;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Database;
using Xunit;

namespace SmartFileAI.Tests;

public sealed class FileRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly FileRepository _repository;

    public FileRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"SmartFileAI_Test_{Guid.NewGuid():N}.db");
        using var db = new SmartFileAIDbContext(_dbPath);
        db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new FileRepository(_dbPath);
    }

    [Fact]
    public async Task InitializeDatabase_ShouldCreateDatabase()
    {
        Assert.True(File.Exists(_dbPath));
        Assert.Empty(await _repository.SearchFilesAsync("nothing"));
    }

    [Fact]
    public async Task BulkInsertOrUpdateAsync_ShouldUpsertByPathCaseInsensitively()
    {
        string parent = Path.Combine(Path.GetTempPath(), "SmartFileAI_Data");
        string path = Path.Combine(parent, "test.txt");
        var first = NewFile(path, parent, 100);
        await _repository.BulkInsertOrUpdateAsync(new[] { first });
        var second = NewFile(path.ToUpperInvariant(), parent.ToUpperInvariant(), 500);
        await _repository.BulkInsertOrUpdateAsync(new[] { second });
        var rows = (await _repository.GetFilesByDirectoryAsync(parent)).ToList();
        Assert.Single(rows);
        Assert.Equal(500, rows[0].Size);
    }

    [Fact]
    public async Task SearchFilesAsync_ShouldEscapeLikeWildcards()
    {
        string parent = Path.Combine(Path.GetTempPath(), "SmartFileAI_Search");
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            NewFile(Path.Combine(parent, "100%real.txt"), parent, 1),
            NewFile(Path.Combine(parent, "plain.txt"), parent, 1)
        });
        var result = (await _repository.SearchFilesAsync("%real")).ToList();
        Assert.Single(result);
        Assert.Equal("100%real.txt", result[0].Name);
    }

    [Fact]
    public async Task DeleteFileRecordAsync_ShouldRemoveDirectoryDescendants()
    {
        string dir = Path.Combine(Path.GetTempPath(), "SmartFileAI_DeleteTree");
        string child = Path.Combine(dir, "child.txt");
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            new FileItem { Name = "SmartFileAI_DeleteTree", FullPath = dir, ParentPath = Path.GetDirectoryName(dir)!, IsDirectory = true, CreatedTime = DateTime.UtcNow, ModifiedTime = DateTime.UtcNow },
            NewFile(child, dir, 1)
        });
        await _repository.DeleteFileRecordAsync(dir);
        Assert.Empty(await _repository.SearchFilesAsync("SmartFileAI_DeleteTree"));
    }

    [Fact]
    public async Task BulkInsert_Benchmark_TenThousandRecords()
    {
        const int count = 10_000;
        string parent = Path.Combine(Path.GetTempPath(), "SmartFileAI_Benchmark");
        var items = Enumerable.Range(0, count).Select(i => NewFile(Path.Combine(parent, $"File_{i}.dat"), parent, i)).ToList();
        var sw = Stopwatch.StartNew();
        await _repository.BulkInsertOrUpdateAsync(items);
        sw.Stop();
        Assert.Equal(count, (await _repository.GetFilesByDirectoryAsync(parent)).Count());
        Trace.WriteLine($"Inserted {count} rows in {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData(".pdf")]
    [InlineData("*.pdf")]
    [InlineData("PDF")]
    [InlineData(".PDF")]
    [InlineData("*.PDF")]
    public async Task SearchFilesAsync_RC223_ExtensionQueries_ReturnOnlyPdf(string query)
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync(query)).ToList();
        var names = results.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(5, names.Count);
        Assert.Equal(new[] { "a.pdf", "b.pdf", "c.pdf", "d.pdf", "report.pdf" }, names);
        Assert.DoesNotContain(results, x => x.Name == "pdf_notes.txt");
        Assert.All(results, x => Assert.Equal(".pdf", x.Extension, ignoreCase: true));
    }

    [Theory]
    [InlineData("code", "code_project.txt")]
    [InlineData("test", "test_100%.txt")]
    public async Task SearchFilesAsync_RC223_ShortWordsNotInExtensionWhitelist_SearchFilename(string query, string expectedName)
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync(query, null, 500, @"D:\Docs")).ToList();

        Assert.Single(results);
        Assert.Equal(expectedName, results[0].Name);
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_FilenameWithExtension_RemainsFilenameSearch()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("report.pdf", null, 500, @"D:\Docs")).ToList();

        Assert.Single(results);
        Assert.Equal("report.pdf", results[0].Name);
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_DriveScope_IsRecursiveAndCaseInsensitive()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("pdf", null, 500, @"d:\")).ToList();
        var names = results.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(4, names.Count);
        Assert.Equal(new[] { "b.pdf", "c.pdf", "d.pdf", "report.pdf" }, names);
        Assert.DoesNotContain(results, x => x.Name == "a.pdf");
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_SubdirectoryScope_EnforcesPathBoundary()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("pdf", null, 500, @"d:\docs")).ToList();
        var names = results.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal(new[] { "b.pdf", "report.pdf" }, names);
        Assert.DoesNotContain(results, x => x.FullPath.StartsWith(@"D:\DocsBackup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_LiteralUnderscore_IsNotLikeWildcard()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("_", null, 500, @"D:\Docs")).ToList();
        var names = results.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(5, names.Count);
        Assert.Equal(new[]
        {
            "annual_report.docx",
            "code_project.txt",
            "file_with_underscore.txt",
            "pdf_notes.txt",
            "test_100%.txt"
        }, names);
        Assert.All(results, x => Assert.Contains("_", x.Name, StringComparison.Ordinal));
    }

    private async Task SeedRc223DataAsync()
    {
        static FileItem Item(string fullPath, long size) => new()
        {
            Name = Path.GetFileName(fullPath),
            FullPath = fullPath,
            ParentPath = Path.GetDirectoryName(fullPath)!,
            Size = size,
            Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
            CreatedTime = DateTime.UtcNow,
            ModifiedTime = DateTime.UtcNow,
            Attributes = FileAttributes.Normal
        };

        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            Item(@"C:\Docs\a.pdf", 100),
            Item(@"D:\Docs\b.pdf", 200),
            Item(@"D:\Other\c.pdf", 300),
            Item(@"D:\DocsBackup\d.pdf", 400),
            Item(@"D:\Docs\annual_report.docx", 500),
            Item(@"D:\Docs\code_project.txt", 600),
            Item(@"D:\Docs\pdf_notes.txt", 700),
            Item(@"D:\Docs\report.pdf", 800),
            Item(@"D:\Docs\test_100%.txt", 900),
            Item(@"D:\Docs\file_with_underscore.txt", 1000)
        });
    }

    private static FileItem NewFile(string path, string parent, long size) => new()
    {
        Name = Path.GetFileName(path), FullPath = path, ParentPath = parent, Size = size,
        Extension = Path.GetExtension(path).ToLowerInvariant(), CreatedTime = DateTime.UtcNow,
        ModifiedTime = DateTime.UtcNow, Attributes = FileAttributes.Normal
    };

    public void Dispose()
    {
        _repository.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
