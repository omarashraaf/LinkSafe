namespace Shared.Models;

public sealed class BackupJobRecord
{
    public string BackupJobId { get; set; } = Guid.NewGuid().ToString("D");
    public string DomainUser { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string BackupType { get; set; } = "Full";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public int FilesProcessed { get; set; }
    public int FilesVersioned { get; set; }
    public string Error { get; set; } = string.Empty;
}
