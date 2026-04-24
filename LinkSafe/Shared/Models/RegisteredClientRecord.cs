namespace Shared.Models;

public sealed class RegisteredClientRecord
{
    public string DomainUser { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string UserFolderName { get; set; } = string.Empty;
    public string ComputerFolderName { get; set; } = string.Empty;
    public string BackupDestinationPath { get; set; } = string.Empty;
    public DateTimeOffset FirstRegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastBackupTime { get; set; }
    public string LastBackupStatus { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = "1.0.0";
    public bool IsDisabled { get; set; }
    public List<string> BackupFoldersOverride { get; set; } = [];
    public List<string> CustomFoldersOverride { get; set; } = [];
}
