using LinkSafeClientAgent.Services;

namespace LinkSafeClientAgent;

public sealed class Worker(BackupCoordinator coordinator, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LinkSafe client agent started.");
        await coordinator.RunAsync(stoppingToken);
    }
}
