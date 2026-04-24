namespace Shared.Models;

public sealed class RoamingRestoreCandidate
{
    public string SourceComputerName { get; set; } = string.Empty;
    public string SourceDomainUser { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public DateTimeOffset? LastBackupTime { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public List<string> AvailableFolders { get; set; } = [];
}
