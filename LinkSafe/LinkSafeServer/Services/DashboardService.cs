using Shared.Models;

namespace LinkSafeServer.Services;

public sealed class DashboardService(
    ServerConfigService serverConfigService,
    ClientRegistryService clientRegistryService,
    BackupJobService backupJobService)
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        var clients = await clientRegistryService.GetAllAsync(cancellationToken);
        var jobs = await backupJobService.GetAllAsync(cancellationToken);

        return new DashboardSummary
        {
            BackupRoot = config.BackupRoot,
            TotalRegisteredClients = clients.Count,
            RecentClients = clients.Count(client => client.LastSeenAt >= DateTimeOffset.UtcNow.AddMinutes(-10)),
            FailedBackupsCount = jobs.Count(job => job.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
            LastBackupTime = jobs.Where(job => job.FinishedAt.HasValue).Select(job => job.FinishedAt).Max(),
            StorageUsageBytes = Directory.Exists(config.BackupRoot)
                ? Directory.EnumerateFiles(config.BackupRoot, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : 0,
            RecentJobs = jobs.Take(10).ToList(),
            RecentErrors = jobs.Where(job => !string.IsNullOrWhiteSpace(job.Error)).Take(10).Select(job => job.Error).ToList()
        };
    }
}
