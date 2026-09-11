using Shared.Models;

namespace LinkSafeServer.Services;

public sealed class DashboardService(
    ServerConfigService serverConfigService,
    ClientRegistryService clientRegistryService,
    BackupJobService backupJobService)
{
    private readonly object _usageGate = new();
    private string? _usageRoot;
    private Task<(long Bytes, string State)>? _usageScan;
    private DateTimeOffset _nextScanAt;

    private (long Bytes, string State) StorageUsage(string root)
    {
        lock (_usageGate)
        {
            // One scan per server at a time; live HTTP requests never wait for disk traversal.
            if (_usageScan is { IsCompleted: false }) return (0, "Calculating...");
            if (_usageScan is { IsCompleted: true } && _nextScanAt == DateTimeOffset.MaxValue)
                _nextScanAt = DateTimeOffset.UtcNow.AddMinutes(1);
            if (_usageScan is null || !string.Equals(_usageRoot, root, StringComparison.OrdinalIgnoreCase) || DateTimeOffset.UtcNow >= _nextScanAt)
            {
                _usageRoot = root;
                _nextScanAt = DateTimeOffset.MaxValue;
                _usageScan = Task.Run(() => MeasureStorage(root));
                return (0, "Calculating...");
            }
            return _usageScan.Result;
        }
    }

    private static (long Bytes, string State) MeasureStorage(string root)
    {
        try
        {
            long bytes = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                try { bytes += new FileInfo(file).Length; }
                catch (FileNotFoundException) { /* An upload/version move can race with measurement. */ }
            }
            return (bytes, "Ready");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (0, "Unavailable"); }
    }

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        var clients = await clientRegistryService.GetAllAsync(cancellationToken);
        var jobs = await backupJobService.GetAllAsync(cancellationToken);
        var usage = StorageUsage(config.BackupRoot);

        return new DashboardSummary
        {
            BackupRoot = config.BackupRoot,
            TotalRegisteredClients = clients.Count,
            RecentClients = clients.Count(client => client.LastSeenAt >= DateTimeOffset.UtcNow.AddMinutes(-10)),
            FailedBackupsCount = jobs.Count(job => job.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
            LastBackupTime = jobs.Where(job => job.FinishedAt.HasValue).Select(job => job.FinishedAt).Max(),
            StorageUsageBytes = usage.Bytes,
            StorageUsageState = usage.State,
            RecentJobs = jobs.Take(10).ToList(),
            RecentErrors = jobs.Where(job => !string.IsNullOrWhiteSpace(job.Error)).Take(10).Select(job => job.Error).ToList()
        };
    }
}
