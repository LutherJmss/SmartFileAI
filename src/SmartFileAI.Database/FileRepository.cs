using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Entities;

namespace SmartFileAI.Database;

public sealed class FileRepository : IFileRepository, IDisposable, IAsyncDisposable
{
    private const int BatchSize = 1000;
    private static readonly HashSet<string> CommonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "txt", "md", "csv", "json", "xml",
        "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "heic",
        "mp3", "wav", "flac", "aac", "m4a",
        "mp4", "mkv", "avi", "mov", "wmv",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx",
        "zip", "rar", "7z",
        "exe", "msi", "dll",
        "cs", "cpp", "h", "java", "py", "js", "ts", "html", "css", "xaml"
    };
    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public FileRepository(string dbPath)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? "smartfile.db" : dbPath;
    }

    public FileRepository(SmartFileAIDbContext context) : this(context.DbPath) { }

    private SmartFileAIDbContext CreateContext() => new(_dbPath);

    public async Task BulkInsertOrUpdateAsync(IEnumerable<FileItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var context = CreateContext();
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            var batch = new List<FileItem>(BatchSize);

            foreach (var source in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Add(NormalizeItem(source));
                if (batch.Count >= BatchSize)
                {
                    await ExecuteBatchUpsertAsync(connection, batch, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await ExecuteBatchUpsertAsync(connection, batch, cancellationToken);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task ExecuteBatchUpsertAsync(SqliteConnection connection, IReadOnlyList<FileItem> items, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = @"
INSERT INTO Files (Name, FullPath, ParentPath, IsDirectory, Size, Extension, CreatedTime, ModifiedTime, Attributes)
VALUES (@Name, @FullPath, @ParentPath, @IsDirectory, @Size, @Extension, @CreatedTime, @ModifiedTime, @Attributes)
ON CONFLICT(FullPath) DO UPDATE SET
    Name = excluded.Name,
    ParentPath = excluded.ParentPath,
    IsDirectory = excluded.IsDirectory,
    Size = excluded.Size,
    Extension = excluded.Extension,
    CreatedTime = excluded.CreatedTime,
    ModifiedTime = excluded.ModifiedTime,
    Attributes = excluded.Attributes;";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var pName = command.Parameters.Add("@Name", SqliteType.Text);
        var pFullPath = command.Parameters.Add("@FullPath", SqliteType.Text);
        var pParentPath = command.Parameters.Add("@ParentPath", SqliteType.Text);
        var pIsDirectory = command.Parameters.Add("@IsDirectory", SqliteType.Integer);
        var pSize = command.Parameters.Add("@Size", SqliteType.Integer);
        var pExtension = command.Parameters.Add("@Extension", SqliteType.Text);
        var pCreatedTime = command.Parameters.Add("@CreatedTime", SqliteType.Text);
        var pModifiedTime = command.Parameters.Add("@ModifiedTime", SqliteType.Text);
        var pAttributes = command.Parameters.Add("@Attributes", SqliteType.Integer);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pName.Value = item.Name;
            pFullPath.Value = item.FullPath;
            pParentPath.Value = item.ParentPath;
            pIsDirectory.Value = item.IsDirectory ? 1 : 0;
            pSize.Value = item.Size;
            pExtension.Value = item.Extension;
            pCreatedTime.Value = item.CreatedTime.ToString("o");
            pModifiedTime.Value = item.ModifiedTime.ToString("o");
            pAttributes.Value = (int)item.Attributes;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<IEnumerable<FileItem>> SearchFilesAsync(string keyword, string? extension = null, int limit = 500)
        => SearchFilesAsync(keyword, extension, limit, scopePath: null);

    public async Task<IEnumerable<FileItem>> SearchFilesAsync(string keyword, string? extension, int limit, string? scopePath)
    {
        ThrowIfDisposed();
        await using var context = CreateContext();
        var query = context.Files.AsNoTracking().AsQueryable();
        limit = Math.Clamp(limit, 1, 5000);

        if (!string.IsNullOrWhiteSpace(scopePath))
        {
            string normalizedScope = NormalizeDirectoryPath(scopePath);
            string normalizedScopeLower = normalizedScope.ToLowerInvariant();
            string scopePrefix = normalizedScope.EndsWith(Path.DirectorySeparatorChar)
                || normalizedScope.EndsWith(Path.AltDirectorySeparatorChar)
                    ? normalizedScope
                    : normalizedScope + Path.DirectorySeparatorChar;
            string scopePrefixLower = scopePrefix.ToLowerInvariant();

            query = query.Where(f =>
                f.FullPath.ToLower() == normalizedScopeLower ||
                f.FullPath.ToLower().StartsWith(scopePrefixLower));
        }

        string trimmedKeyword = keyword?.Trim() ?? string.Empty;
        string? effectiveExtension = NormalizeExtension(extension);

        if (string.IsNullOrWhiteSpace(effectiveExtension)
            && TryParseExtensionQuery(trimmedKeyword, out string? parsedExtension))
        {
            effectiveExtension = parsedExtension;
            trimmedKeyword = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(trimmedKeyword))
        {
            string pattern = $"%{EscapeLikePattern(trimmedKeyword)}%";
            query = query.Where(f => EF.Functions.Like(f.Name, pattern, "\\"));
        }

        if (!string.IsNullOrWhiteSpace(effectiveExtension))
        {
            string extLower = effectiveExtension.ToLowerInvariant();
            query = query.Where(f => f.Extension.ToLower() == extLower);
        }

        return await query
            .OrderByDescending(f => f.IsDirectory)
            .ThenBy(f => f.Name)
            .Take(limit)
            .ToListAsync();
    }

    private static bool TryParseExtensionQuery(string keyword, out string? extension)
    {
        extension = null;
        if (string.IsNullOrWhiteSpace(keyword)) return false;

        string candidate = keyword.Trim();
        if (candidate.StartsWith("*.", StringComparison.Ordinal) && candidate.Length > 2)
        {
            string raw = candidate[2..];
            if (IsSimpleExtensionToken(raw))
            {
                extension = "." + raw.ToLowerInvariant();
                return true;
            }
            return false;
        }

        if (candidate.StartsWith(".", StringComparison.Ordinal) && candidate.Length > 1)
        {
            string raw = candidate[1..];
            if (IsSimpleExtensionToken(raw))
            {
                extension = "." + raw.ToLowerInvariant();
                return true;
            }
            return false;
        }

        if (!candidate.Contains('.') && !candidate.Contains('*') && CommonExtensions.Contains(candidate))
        {
            extension = "." + candidate.ToLowerInvariant();
            return true;
        }

        return false;
    }

    private static bool IsSimpleExtensionToken(string value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Contains('.')
           && !value.Contains('*')
           && !value.Contains(Path.DirectorySeparatorChar)
           && !value.Contains(Path.AltDirectorySeparatorChar)
           && value.All(ch => !char.IsWhiteSpace(ch));

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        string normalized = extension.Trim().ToLowerInvariant();
        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    public async Task<IEnumerable<FileItem>> GetFilesByDirectoryAsync(string directoryPath)
    {
        ThrowIfDisposed();
        string normalized = NormalizeDirectoryPath(directoryPath);
        await using var context = CreateContext();
        return await context.Files.AsNoTracking()
            .Where(f => f.ParentPath == normalized)
            .OrderByDescending(f => f.IsDirectory)
            .ThenBy(f => f.Name)
            .ToListAsync();
    }

    public async Task DeleteFileRecordAsync(string fullPath)
    {
        ThrowIfDisposed();
        string normalized = NormalizePath(fullPath);
        string prefix = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;

        await _writeGate.WaitAsync();
        try
        {
            await using var context = CreateContext();
            await context.Database.ExecuteSqlInterpolatedAsync($@"
DELETE FROM Files
WHERE FullPath = {normalized} COLLATE NOCASE
   OR substr(FullPath, 1, {prefix.Length}) = {prefix} COLLATE NOCASE;");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task LogOperationAsync(OperationLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        ThrowIfDisposed();
        await _writeGate.WaitAsync();
        try
        {
            await using var context = CreateContext();
            await context.OperationLogs.AddAsync(log);
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> RecordScanStartAsync(string drive)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync();
        try
        {
            await using var context = CreateContext();
            var scan = new ScanHistory
            {
                Drive = NormalizeDirectoryPath(drive),
                StartTime = DateTime.UtcNow,
                IsCompleted = false
            };
            await context.ScanHistories.AddAsync(scan);
            await context.SaveChangesAsync();
            return scan.Id;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CompleteScanAsync(int scanId, long fileCount, long totalSize)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync();
        try
        {
            await using var context = CreateContext();
            var history = await context.ScanHistories.FindAsync(scanId);
            if (history is null) return;
            history.EndTime = DateTime.UtcNow;
            history.FileCount = fileCount;
            history.TotalSizeBytes = totalSize;
            history.IsCompleted = true;
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static FileItem NormalizeItem(FileItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        FullPath = NormalizePath(item.FullPath),
        ParentPath = NormalizeDirectoryPath(item.ParentPath),
        IsDirectory = item.IsDirectory,
        Size = item.Size,
        Extension = item.Extension?.ToLowerInvariant() ?? string.Empty,
        CreatedTime = item.CreatedTime,
        ModifiedTime = item.ModifiedTime,
        Attributes = item.Attributes
    };

    private static string NormalizeDirectoryPath(string path)
    {
        string full = NormalizePath(path);
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return root;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path cannot be empty.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static string EscapeLikePattern(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FileRepository));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeGate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
