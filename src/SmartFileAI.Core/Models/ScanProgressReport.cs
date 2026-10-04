namespace SmartFileAI.Core.Models;

public record ScanProgressReport(
    string CurrentDirectory,
    long ScannedFilesCount,
    long ScannedDirectoriesCount,
    long TotalScannedSizeBytes,
    string? WarningMessage = null
);
