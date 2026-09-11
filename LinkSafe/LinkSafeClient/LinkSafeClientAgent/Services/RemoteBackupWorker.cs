using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class RemoteBackupWorker(AgentConfigService configs, ClientIdentityService identities,
    ClientApiService api, RemoteBackupInbox inbox, AgentStatusService statuses, ILogger<RemoteBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = await configs.GetAsync(stoppingToken);
                var identity = identities.GetCurrent();
                var local = await inbox.ReadAsync(config.ServerUrl, stoppingToken);
                var report = new RemoteBackupPoll { DomainUser = identity.DomainUser, ComputerName = identity.ComputerName,
                    RequestId = local?.RequestId ?? "", Status = local?.Status ?? "Queued", Message = local?.Message ?? "" };
                if (local is { IsTerminal: false })
                {
                    if (!config.IsAgentEnabled || !string.IsNullOrEmpty(await new BackupStopControl(LinkSafeDefaults.ClientStopBackupSignalPath).ReadAsync(stoppingToken)))
                    {
                        report.Status = "Blocked";
                        report.Message = "Agent is disabled or backups are paused on this PC. Enable/resume locally to continue.";
                    }
                    else
                    {
                        var status = await statuses.GetAsync(stoppingToken);
                        if (status.LastOperationRequestId == local.RequestId && status.LastOperationFinishedAt is null)
                        {
                            report.Status = "Running";
                            report.Message = status.LastOperationMessage;
                        }
                    }
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var pending = await api.PollBackupRequestAsync(config, report, timeout.Token);
                if (pending is not null && pending.Matches(identity.DomainUser, identity.ComputerName))
                    await inbox.AcceptAsync(config.ServerUrl, pending, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Server backup request poll failed; will retry without interrupting backups."); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
