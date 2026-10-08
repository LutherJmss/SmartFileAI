using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;

namespace SmartFileAI.Core.Interfaces;

public interface IFileRepository
{
    Task BulkInsertOrUpdateAsync(IEnumerable<FileItem> items, CancellationToken cancellationToken = default);
    Task<IEnumerable<FileItem>> SearchFilesAsync(string keyword, string? extension = null, int limit = 500);

    // Backward-compatible scoped-search overload. Existing test doubles and callers that only
    // implement/use the original three-parameter API continue to work.
    Task<IEnumerable<FileItem>> SearchFilesAsync(string keyword, string? extension, int limit, string? scopePath)
        => SearchFilesAsync(keyword, extension, limit);

    // Compatibility adapters preserve existing implementations and test doubles.
    // Repositories supporting persistent sorting override these explicit overloads.
    Task<IEnumerable<FileItem>> SearchFilesAsync(string keyword, string? extension, int limit, string? scopePath, FileSortMode sortMode)
        => SearchFilesAsync(keyword, extension, limit, scopePath);

    Task<IEnumerable<FileItem>> GetFilesByDirectoryAsync(string directoryPath);
    Task<IEnumerable<FileItem>> GetFilesByDirectoryAsync(string directoryPath, FileSortMode sortMode)
        => GetFilesByDirectoryAsync(directoryPath);
    Task DeleteFileRecordAsync(string fullPath);
    Task LogOperationAsync(OperationLog log);
    Task<int> RecordScanStartAsync(string drive);
    Task CompleteScanAsync(int scanId, long fileCount, long totalSize);
}
