namespace Shared.Models;

public sealed class HeartbeatRequest
{
    public string DomainUser { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public DateTimeOffset? LastBackupTime { get; set; }
    public string LastBackupStatus { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
}
