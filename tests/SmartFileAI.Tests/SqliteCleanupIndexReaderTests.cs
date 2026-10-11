using Microsoft.Data.Sqlite;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Database;
using Xunit;

namespace SmartFileAI.Tests;

public sealed class SqliteCleanupIndexReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmartFileAI_IndexReader_" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SqliteCleanupIndexReader _reader;

    public SqliteCleanupIndexReaderTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "index.db");
        using var db = new SmartFileAIDbContext(_dbPath);
        db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _reader = new SqliteCleanupIndexReader(_dbPath);
    }

    [Fact]
    public async Task ScopedPages_AreOrderedUniqueAndBounded()
    {
        await SeedAsync(Enumerable.Range(1, 700).Select(i => Item(i, $@"D:\Docs\file{i:D4}.tmp")).ToArray());
        var ids = new List<long>(); long cursor = 0; int pages = 0;
        while (true)
        {
            var page = await _reader.GetIndexPageAsync(@"D:\Docs", cursor, 256);
            if (page.Count == 0) break;
            Assert.InRange(page.Count, 1, 256);
            Assert.True(page[0].Id > cursor);
            Assert.Equal(page.Select(x => x.Id).OrderBy(x => x), page.Select(x => x.Id));
            ids.AddRange(page.Select(x => x.Id)); cursor = page[^1].Id; pages++;
        }
        Assert.Equal(3, pages);
        Assert.Equal(700, ids.Count);
        Assert.Equal(700, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 700).Select(x => (long)x), ids);
    }

    [Fact]
    public async Task Scope_MatchesSelfAndNestedChildren_ButNeverBackupOrOtherDrive()
    {
        await SeedAsync(Item(1, @"D:\Docs"), Item(2, @"D:\Docs\a.tmp"), Item(3, @"D:\Docs\Sub\b.tmp"),
            Item(4, @"D:\DocsBackup\c.tmp"), Item(5, @"C:\Docs\d.tmp"));
        var page = await _reader.GetIndexPageAsync(@"d:\docs\", 0, 10);
        Assert.Equal(new long[] { 1, 2, 3 }, page.Select(x => x.Id));
    }

    [Fact]
    public async Task DriveRoot_DoesNotLeakAnotherDrive()
    {
        await SeedAsync(Item(1, @"D:\"), Item(2, @"D:\Docs\a.tmp"), Item(3, @"D:\Other\b.tmp"), Item(4, @"C:\Docs\c.tmp"));
        Assert.Equal(new long[] { 1, 2, 3 }, (await _reader.GetIndexPageAsync(@"d:/", 0, 10)).Select(x => x.Id));
    }

    [Fact]
    public async Task Scope_UsesUnicodeOrdinalIgnoreCase_NotSqliteAsciiNoCase()
    {
        await SeedAsync(Item(1, @"D:\Döcs\a.tmp"), Item(2, @"D:\DöcsBackup\b.tmp"));
        Assert.Equal(1, Assert.Single(await _reader.GetIndexPageAsync(@"d:\DÖCS", 0, 10)).Id);
    }

    [Fact]
    public async Task Scope_PercentUnderscoreAndSupplementaryCharactersAreLiteral()
    {
        await SeedAsync(Item(1, @"D:\100%_😀\a.tmp"), Item(2, @"D:\100ZZ😀\b.tmp"), Item(3, @"D:\100%_😀Backup\c.tmp"));
        Assert.Equal(1, Assert.Single(await _reader.GetIndexPageAsync(@"D:\100%_😀", 0, 10)).Id);
    }

    [Fact]
    public async Task EmptyScopeResults_ReturnEmptyPage()
    {
        await SeedAsync(Item(1, @"C:\Other\a.tmp"));
        Assert.Empty(await _reader.GetIndexPageAsync(@"D:\Empty", 0, 10));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative")]
    [InlineData(@"D:Docs")]
    [InlineData(@"\Docs")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\D:\Docs")]
    [InlineData(@"D:\Docs:stream")]
    [InlineData(@"D:\Do*cs")]
    public async Task InvalidScope_IsRejectedBeforeReadingDatabase(string scope)
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => _reader.GetIndexPageAsync(scope, 0, 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public async Task InvalidPageSize_IsRejected(int pageSize)
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _reader.GetIndexPageAsync(@"D:\Docs", 0, pageSize));

    [Fact]
    public async Task NegativeCursor_IsRejected()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _reader.GetIndexPageAsync(@"D:\Docs", -1, 10));

    [Fact]
    public async Task PreCanceledRead_ThrowsCancellation()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.GetIndexPageAsync(@"D:\Docs", 0, 10, cts.Token));
    }

    [Fact]
    public async Task ConcurrentReads_DoNotShareDbContextOrTrackEntities()
    {
        await SeedAsync(Item(1, @"D:\Docs\a.tmp"), Item(2, @"D:\Docs\b.tmp"));
        var pages = await Task.WhenAll(_reader.GetIndexPageAsync(@"D:\Docs", 0, 1), _reader.GetIndexPageAsync(@"D:\Docs", 1, 1));
        Assert.Equal(1, Assert.Single(pages[0]).Id);
        Assert.Equal(2, Assert.Single(pages[1]).Id);
        pages[0][0].Name = "mutated detached object";
        Assert.Equal("a.tmp", Assert.Single(await _reader.GetIndexPageAsync(@"D:\Docs", 0, 1)).Name);
    }

    [Fact]
    public async Task MissingDatabase_IsNotCreatedByReadOnlyReader()
    {
        string missing = Path.Combine(_root, "missing.db");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteCleanupIndexReader(missing).GetIndexPageAsync(@"D:\Docs", 0, 10));
        Assert.False(File.Exists(missing));
    }

    private async Task SeedAsync(params FileItem[] items)
    {
        await using var db = new SmartFileAIDbContext(_dbPath);
        db.Files.AddRange(items); // Fixture only; the production batch UPSERT is untouched.
        await db.SaveChangesAsync();
    }
    private static FileItem Item(long id, string fullPath)
    {
        int separator = fullPath.LastIndexOf('\\');
        return new FileItem { Id = id, Name = fullPath[(separator + 1)..], FullPath = fullPath,
            ParentPath = separator > 2 ? fullPath[..separator] : fullPath[..3], Size = id, Extension = ".tmp" };
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
