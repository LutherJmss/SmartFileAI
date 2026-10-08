using System.Diagnostics;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;
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

        var results = (await _repository.SearchFilesAsync(query, null, 500, @"D:Docs")).ToList();

        Assert.Single(results);
        Assert.Equal(expectedName, results[0].Name);
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_FilenameWithExtension_RemainsFilenameSearch()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("report.pdf", null, 500, @"D:Docs")).ToList();

        Assert.Single(results);
        Assert.Equal("report.pdf", results[0].Name);
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_DriveScope_IsRecursiveAndCaseInsensitive()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("pdf", null, 500, @"d:")).ToList();
        var names = results.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(4, names.Count);
        Assert.Equal(new[] { "b.pdf", "c.pdf", "d.pdf", "report.pdf" }, names);
        Assert.DoesNotContain(results, x => x.Name == "a.pdf");
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_SubdirectoryScope_EnforcesPathBoundary()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("pdf", null, 500, @"d:docs")).ToList();
        var names = results.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(2, names.Count);
        Assert.Equal(new[] { "b.pdf", "report.pdf" }, names);
        Assert.DoesNotContain(results, x => x.FullPath.StartsWith(@"D:DocsBackup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchFilesAsync_RC223_LiteralUnderscore_IsNotLikeWildcard()
    {
        await SeedRc223DataAsync();

        var results = (await _repository.SearchFilesAsync("_", null, 500, @"D:Docs")).ToList();
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

    [Fact]
    public async Task Phase7A_NameAscending_PreservesDefaultOverloads()
    {
        string parent = NewSortingParent();
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            SortingItem(parent, "Zebra.txt", 1),
            SortingItem(parent, "Beta", 2, isDirectory: true),
            SortingItem(parent, "Apple.txt", 3),
            SortingItem(parent, "Alpha", 4, isDirectory: true)
        });
        var expected = new[] { "Alpha", "Beta", "Apple.txt", "Zebra.txt" };

        Assert.Equal(expected, (await _repository.GetFilesByDirectoryAsync(parent, FileSortMode.NameAscending)).Select(x => x.Name));
        Assert.Equal(expected, (await _repository.GetFilesByDirectoryAsync(parent)).Select(x => x.Name));
        Assert.Equal(expected, (await _repository.SearchFilesAsync(string.Empty)).Select(x => x.Name));
        Assert.Equal(expected, (await _repository.SearchFilesAsync(string.Empty, null, 500, parent)).Select(x => x.Name));
    }

    [Fact]
    public async Task Phase7A_ModifiedNewest_OrdersByDescendingModifiedTime()
    {
        string parent = NewSortingParent();
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            SortingItem(parent, "Alpha.txt", 1),
            SortingItem(parent, "Middle.txt", 2),
            SortingItem(parent, "Zebra.txt", 3)
        });

        var results = await _repository.GetFilesByDirectoryAsync(parent, FileSortMode.ModifiedNewest);
        Assert.Equal(new[] { "Zebra.txt", "Middle.txt", "Alpha.txt" }, results.Select(x => x.Name));
    }

    [Fact]
    public async Task Phase7A_ModifiedOldest_OrdersByAscendingModifiedTime()
    {
        string parent = NewSortingParent();
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            SortingItem(parent, "Alpha.txt", 3),
            SortingItem(parent, "Middle.txt", 2),
            SortingItem(parent, "Zebra.txt", 1)
        });

        var results = await _repository.GetFilesByDirectoryAsync(parent, FileSortMode.ModifiedOldest);
        Assert.Equal(new[] { "Zebra.txt", "Middle.txt", "Alpha.txt" }, results.Select(x => x.Name));
    }

    [Fact]
    public async Task Phase7A_AllSortModes_KeepDirectoriesBeforeFiles()
    {
        string parent = NewSortingParent();
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            SortingItem(parent, "A_File.txt", 100),
            SortingItem(parent, "B_File.txt", -100),
            SortingItem(parent, "Z_Directory", 1, isDirectory: true),
            SortingItem(parent, "Y_Directory", 2, isDirectory: true)
        });

        foreach (var mode in Enum.GetValues<FileSortMode>())
        {
            var directoryRows = (await _repository.GetFilesByDirectoryAsync(parent, mode)).ToList();
            var searchRows = (await _repository.SearchFilesAsync(string.Empty, null, 500, parent, mode)).ToList();
            foreach (var rows in new[] { directoryRows, searchRows })
            {
                Assert.Equal(4, rows.Count);
                Assert.All(rows.Take(2), item => Assert.True(item.IsDirectory));
                Assert.All(rows.Skip(2), item => Assert.False(item.IsDirectory));
            }
        }
    }

    [Fact]
    public async Task Phase7A_EqualModifiedTimes_UseDeterministicNameTieBreak()
    {
        string parent = NewSortingParent();
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            SortingItem(parent, "match_Zebra.txt", 1),
            SortingItem(parent, "match_Apple.txt", 1),
            SortingItem(parent, "match_Middle.txt", 1)
        });
        var expected = new[] { "match_Apple.txt", "match_Middle.txt", "match_Zebra.txt" };

        foreach (var mode in new[] { FileSortMode.ModifiedNewest, FileSortMode.ModifiedOldest })
        {
            Assert.Equal(expected, (await _repository.GetFilesByDirectoryAsync(parent, mode)).Select(x => x.Name));
            Assert.Equal(expected, (await _repository.SearchFilesAsync("match", null, 500, parent, mode)).Select(x => x.Name));
        }
    }

    [Fact]
    public async Task Phase7A_ScopedModifiedNewest_PreservesExtensionParsingAndBoundary()
    {
        await AssertSortedScopeAsync(FileSortMode.ModifiedNewest, new[] { "b.pdf", "a.pdf" });
    }

    [Fact]
    public async Task Phase7A_ScopedModifiedOldest_PreservesExtensionParsingAndBoundary()
    {
        await AssertSortedScopeAsync(FileSortMode.ModifiedOldest, new[] { "a.pdf", "b.pdf" });
    }

    [Fact]
    public async Task Phase7A_ModifiedNewest_SortsBeforeTakingFiveHundredResults()
    {
        string parent = NewSortingParent();
        var items = Enumerable.Range(0, 550)
            .Select(i => SortingItem(parent, $"match_{i:D4}.txt", i)).ToList();
        // Last inserted and last alphabetically: excluded by taking insertion/name order first.
        var newest = SortingItem(parent, "match_zz_latest.txt", 10_000);
        items.Add(newest);
        await _repository.BulkInsertOrUpdateAsync(items);
        Assert.Equal(551, (await _repository.GetFilesByDirectoryAsync(parent)).Count());

        var results = (await _repository.SearchFilesAsync("match", null, 500, parent, FileSortMode.ModifiedNewest)).ToList();

        Assert.Equal(500, results.Count);
        Assert.Equal(newest.FullPath, results[0].FullPath);
        Assert.Contains(results, item => item.FullPath == newest.FullPath);
        Assert.Equal("match_0051.txt", results[^1].Name);
        Assert.DoesNotContain(results, item => item.Name == "match_0000.txt");
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].ModifiedTime >= results[i].ModifiedTime);
    }

    private async Task AssertSortedScopeAsync(FileSortMode mode, string[] expected)
    {
        string root = NewSortingParent();
        string scope = Path.Combine(root, "Docs");
        string nested = Path.Combine(scope, "Sub");
        await _repository.BulkInsertOrUpdateAsync(new[]
        {
            SortingItem(scope, "a.pdf", 1),
            SortingItem(nested, "b.pdf", 2),
            SortingItem(scope, "pdf_notes.txt", 100),
            SortingItem(Path.Combine(root, "DocsBackup"), "outside.pdf", 200),
            SortingItem(Path.Combine(root, "Other"), "other.pdf", -200)
        });

        foreach (var keyword in new[] { "pdf", ".pdf", "*.pdf", "PDF", ".PDF", "*.PDF" })
        {
            var rows = (await _repository.SearchFilesAsync(keyword, null, 500, scope.ToUpperInvariant(), mode)).ToList();
            Assert.Equal(expected, rows.Select(x => x.Name));
            Assert.All(rows, item => Assert.Equal(".pdf", item.Extension, ignoreCase: true));
        }
    }

    private static string NewSortingParent()
        => Path.Combine(Path.GetTempPath(), "SmartFileAI_Sorting_" + Guid.NewGuid().ToString("N"));

    private static FileItem SortingItem(string parent, string name, int modifiedDay, bool isDirectory = false)
    {
        var item = NewFile(Path.Combine(parent, name), parent, isDirectory ? 0 : 42);
        item.IsDirectory = isDirectory;
        item.ModifiedTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(modifiedDay);
        item.Extension = isDirectory ? string.Empty : item.Extension;
        item.Attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        return item;
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
            Item(@"C:Docsa.pdf", 100),
            Item(@"D:Docs.pdf", 200),
            Item(@"D:Otherc.pdf", 300),
            Item(@"D:DocsBackupd.pdf", 400),
            Item(@"D:Docsannual_report.docx", 500),
            Item(@"D:Docscode_project.txt", 600),
            Item(@"D:Docspdf_notes.txt", 700),
            Item(@"D:Docseport.pdf", 800),
            Item(@"D:Docs	est_100%.txt", 900),
            Item(@"D:Docsile_with_underscore.txt", 1000)
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
