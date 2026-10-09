using SmartFileAI.Core.Models.Cleanup;
using SmartFileAI.Core.Models.Entities;

namespace SmartFileAI.Core.Interfaces;

public interface ICleanupAnalyzer
{
    /// <summary>
    /// Read-only analysis using an explicit UTC reference time and live filesystem evidence.
    /// Null means no applicable rule. A candidate never grants permission to delete.
    /// </summary>
    CleanupCandidate? Analyze(FileItem item, DateTime referenceTime);
}
