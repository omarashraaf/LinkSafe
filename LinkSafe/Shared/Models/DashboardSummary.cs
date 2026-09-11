namespace Shared.Models;

public sealed class DashboardSummary
{
    public string ServerStatus { get; set; } = "Running";
    public string BackupRoot { get; set; } = string.Empty;
    public int TotalRegisteredClients { get; set; }
    public int RecentClients { get; set; }
    public int FailedBackupsCount { get; set; }
    public DateTimeOffset? LastBackupTime { get; set; }
    public long StorageUsageBytes { get; set; }
    public string StorageUsageState { get; set; } = "Calculating...";
    public List<BackupJobRecord> RecentJobs { get; set; } = [];
    public List<string> RecentErrors { get; set; } = [];
}
