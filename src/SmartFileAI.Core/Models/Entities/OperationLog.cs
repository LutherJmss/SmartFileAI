using SmartFileAI.Core.Models.Enums;

namespace SmartFileAI.Core.Models.Entities;

public class OperationLog
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public FileOperationType Operation { get; set; }
    public OperationResultType Result { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
}
