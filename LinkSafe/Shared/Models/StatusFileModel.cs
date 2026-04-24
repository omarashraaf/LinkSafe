namespace Shared.Models;

public sealed class StatusFileModel
{
    public string ClientId { get; set; } = string.Empty;
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool AgentEnabled { get; set; }
    public bool Registered { get; set; }
    public bool ServerReachable { get; set; }
    public DateTimeOffset? LastRun { get; set; }
    public DateTimeOffset? LastFullBackup { get; set; }
    public DateTimeOffset? LastSuccessfulBackup { get; set; }
    public DateTimeOffset? LastOperationStartedAt { get; set; }
    public DateTimeOffset? LastOperationFinishedAt { get; set; }
    public string LastOperationStatus { get; set; } = string.Empty;
    public string LastOperationMessage { get; set; } = string.Empty;
    public DateTimeOffset? LastRestoreAt { get; set; }
    public string LastRestoreSourceComputer { get; set; } = string.Empty;
    public DateTimeOffset? LastRestorePromptedAt { get; set; }
    public string LastRestorePromptSourceComputer { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public string LastBackupStatus { get; set; } = string.Empty;
}
