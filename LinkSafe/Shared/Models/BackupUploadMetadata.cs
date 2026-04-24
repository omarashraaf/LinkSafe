namespace Shared.Models;

public sealed class BackupUploadMetadata
{
    public string DomainUser { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = "1.0.0";
    public string BackupJobId { get; set; } = Guid.NewGuid().ToString("D");
    public string BackupType { get; set; } = "Full";
    public DateTimeOffset BackupStartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset BackupFinishedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> IncludedFolders { get; set; } = [];
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
}
