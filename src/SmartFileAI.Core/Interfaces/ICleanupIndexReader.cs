using SmartFileAI.Core.Models.Entities;

namespace SmartFileAI.Core.Interfaces;

public interface ICleanupIndexReader
{
    /// <summary>Read one bounded index page, ordered by strictly increasing Id; no filesystem scan.</summary>
    Task<IReadOnlyList<FileItem>> GetIndexPageAsync(string scopePath, long lastSeenId, int pageSize,
        CancellationToken cancellationToken = default);
}
