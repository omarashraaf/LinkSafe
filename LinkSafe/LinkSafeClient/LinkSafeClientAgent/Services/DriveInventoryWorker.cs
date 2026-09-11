namespace LinkSafeClientAgent.Services;

public sealed class DriveInventoryWorker(
    AgentConfigService configService,
    ClientIdentityService identityService,
    ClientApiService apiService,
    ILogger<DriveInventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tracker = new DriveInventoryTracker();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = await configService.GetAsync(stoppingToken);
                var drives = FolderMappingService.GetAvailableDrives();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await tracker.RefreshAsync(config.ServerUrl, drives, async () =>
                {
                    var response = await apiService.RegisterAsync(
                        config, identityService.GetCurrent(), timeout.Token, drives);
                    if (response?.Success != true)
                        throw new InvalidOperationException("Server did not accept the drive inventory.");
                    logger.LogInformation("Drive inventory updated: {Drives}.", string.Join(", ", drives));
                    return true;
                });
                // Refresh independently of the backup/upload loop, even when drives did not change.
                using var policyTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                policyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var policy = await apiService.GetPolicyAsync(config, policyTimeout.Token);
                if (policy is not null)
                    await configService.Policies.SavePolicyAsync(config.ServerUrl, policy, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Drive inventory refresh failed; will retry independently of backups.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

public sealed class DriveInventoryTracker
{
    private string? _serverUrl;
    private HashSet<string>? _reportedDrives;
    private DateTimeOffset _lastReported;

    public async Task RefreshAsync(string serverUrl, IEnumerable<string> drives, Func<Task<bool>> publish)
    {
        var snapshot = drives.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(_serverUrl, serverUrl, StringComparison.OrdinalIgnoreCase) &&
            _reportedDrives?.SetEquals(snapshot) == true &&
            DateTimeOffset.UtcNow - _lastReported < TimeSpan.FromMinutes(2)) return;

        // Only acknowledge successful reports, so disconnected servers are retried.
        if (!await publish()) return;
        _serverUrl = serverUrl;
        _reportedDrives = snapshot;
        _lastReported = DateTimeOffset.UtcNow;
    }
}
