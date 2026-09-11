namespace Shared.Models;

public sealed class RemoteBackupRequest
{
    public string RequestId { get; set; } = "";
    public string DomainUser { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "Waiting for the agent to check in.";
    public bool IsTerminal => Status is "Success" or "NoChanges" or "Failed" or "Stopped";
    public static bool Supports(string version) => Version.TryParse(version, out var parsed) && parsed >= new Version(1, 0, 16);
    public bool Matches(string user, string computer) =>
        string.Equals(DomainUser, user, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ComputerName, computer, StringComparison.OrdinalIgnoreCase);
}

public sealed class RemoteBackupPoll
{
    public string DomainUser { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "";
}
