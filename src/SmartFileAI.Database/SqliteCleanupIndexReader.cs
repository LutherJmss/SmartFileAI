using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Cleanup;
using SmartFileAI.Core.Models.Entities;

namespace SmartFileAI.Database;

public sealed class SqliteCleanupIndexReader : ICleanupIndexReader
{
    private readonly string _dbPath;
    public SqliteCleanupIndexReader(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
    }

    public async Task<IReadOnlyList<FileItem>> GetIndexPageAsync(string scopePath, long lastSeenId, int pageSize,
        CancellationToken cancellationToken = default)
    {
        string scope = CleanupScopePath.Normalize(scopePath);
        if (lastSeenId < 0) throw new ArgumentOutOfRangeException(nameof(lastSeenId));
        if (pageSize is < 1 or > CleanupScopePath.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        cancellationToken.ThrowIfCancellationRequested();
        string prefix = CleanupScopePath.DescendantPrefix(scope);
        // Separators are ASCII. [prefix, successor) matches whole descendant paths, never DocsBackup.
        string successor = prefix[..^1] + (char)(prefix[^1] + 1);
        var builder = new SqliteConnectionStringBuilder
            { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        // SQLite NOCASE only folds ASCII. Use the same .NET ordinal ignore-case comparison for Unicode paths too.
        connection.CreateCollation("CLEANUP_PATH", StringComparer.OrdinalIgnoreCase.Compare);
        var options = new DbContextOptionsBuilder<SmartFileAIDbContext>().UseSqlite(connection).Options;
        await using var context = new SmartFileAIDbContext(options);
        return await context.Files.AsNoTracking()
            .Where(f => f.Id > lastSeenId)
            .Where(f => EF.Functions.Collate(f.FullPath, "CLEANUP_PATH") == scope
                || (EF.Functions.Collate(f.FullPath, "CLEANUP_PATH").CompareTo(prefix) >= 0
                    && EF.Functions.Collate(f.FullPath, "CLEANUP_PATH").CompareTo(successor) < 0))
            .OrderBy(f => f.Id).Take(pageSize).ToListAsync(cancellationToken);
    }
}
