namespace SmartFileAI.FileOperations.Safety;

public enum DeletionRejectionReason
{
    None,
    EmptyOrWhitespacePath,
    NotRootedOrRelativePath,
    ExtendedDevicePath,
    UncPath,
    DriveRootDirectory,
    SystemProtectedDirectory,
    ReparsePointOrLinkDetected,
    AlternateDataStream,
    PathNotFound,
    SecurityCheckException
}

public sealed record DeletionSafetyResult(bool IsPermitted, DeletionRejectionReason Reason, string? Details = null)
{
    public static DeletionSafetyResult Allowed() => new(true, DeletionRejectionReason.None);

    public static DeletionSafetyResult Denied(DeletionRejectionReason reason, string details) =>
        new(false, reason, details);
}
