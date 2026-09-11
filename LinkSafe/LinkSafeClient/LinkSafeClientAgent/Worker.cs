using LinkSafeClientAgent.Services;

namespace LinkSafeClientAgent;

public sealed class Worker(BackupCoordinator coordinator, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LinkSafe client agent started.");
        try { await coordinator.RunAsync(stoppingToken); }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Task Scheduler retries only failed processes, not a successful host shutdown.
            Environment.ExitCode = 1;
            throw;
        }
    }
}
