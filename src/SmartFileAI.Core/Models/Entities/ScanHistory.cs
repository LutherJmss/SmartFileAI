namespace SmartFileAI.Core.Models.Entities;

public class ScanHistory
{
    public int Id { get; set; }
    public string Drive { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public long FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public bool IsCompleted { get; set; }
}
