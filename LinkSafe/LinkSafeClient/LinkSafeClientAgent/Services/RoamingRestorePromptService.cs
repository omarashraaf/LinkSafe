using System.Windows.Forms;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class RoamingRestorePromptService(
    AgentStatusService statusService,
    ClientIdentityService clientIdentityService,
    ClientApiService clientApiService,
    FolderMappingService folderMappingService,
    ILogger<RoamingRestorePromptService> logger)
{
    public async Task OfferRestoreIfNeededAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.ServerUrl)) return;
        var identity = clientIdentityService.GetCurrent();
        var status = await statusService.GetAsync(cancellationToken);
        var options = await clientApiService.GetRoamingRestoreOptionsAsync(config, identity, cancellationToken);
        var candidate = options?.Candidates
            .OrderByDescending(item => item.SourceComputerName.Equals(status.AutoRestoreSourceComputer, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.LastBackupTime ?? item.LastSeenAt)
            .FirstOrDefault();
        if (candidate is null) return;

        // Compare on each logon, even if the server timestamp has not changed:
        // the user may have lost a local file since the previous restore.
        var roots = MissingFileRestore.BuildRoots(config,
            name => folderMappingService.TryResolveRestoreFolder(name, out var path) ? path : "");
        var plan = await clientApiService.CompareRestoreAsync(config, identity.DomainUser, candidate.SourceComputerName, roots, cancellationToken);
        if (plan.Missing.Count == 0)
        {
            logger.LogInformation("Restore comparison: {ExistingCount} existing files preserved; {WarningCount} warnings.", plan.Existing, plan.Warnings.Count);
            if (plan.Warnings.Count > 0)
            {
                status = await statusService.GetAsync(cancellationToken);
                status.LastError = "Restore comparison: " + string.Join("; ", plan.Warnings.Take(3));
                await statusService.SaveAsync(status, cancellationToken);
            }
            return;
        }

        var automatic = status.AutoRestoreEnabled &&
            candidate.SourceComputerName.Equals(status.AutoRestoreSourceComputer, StringComparison.OrdinalIgnoreCase);
        if (!automatic || status.AskBeforeRestoreEveryTime)
        {
            var result = MessageBox.Show(
                $"LinkSafe found {plan.Missing.Count:N0} missing files for {identity.DomainUser} from {candidate.SourceComputerName}.\n\n" +
                $"{plan.Existing:N0} existing local files will NOT be replaced. {plan.Warnings.Count:N0} comparison warnings.\n\nRestore only the missing files now?",
                "LinkSafe Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2, MessageBoxOptions.DefaultDesktopOnly);
            if (result != DialogResult.Yes) return;
        }

        status = await statusService.GetAsync(cancellationToken);
        status.AutoRestoreEnabled = true;
        status.AutoRestoreSourceComputer = candidate.SourceComputerName;
        await statusService.SaveAsync(status, cancellationToken);
        try
        {
            var result = await clientApiService.RestoreMissingAsync(config, plan, (message, percent) =>
            {
                logger.LogInformation("Restore progress {Percent}%: {Message}", percent, message);
                return Task.CompletedTask;
            }, cancellationToken);
            status = await statusService.GetAsync(cancellationToken);
            if (result.Errors.Count == 0) status.LastRestoreAt = DateTimeOffset.UtcNow;
            status.LastRestoreSourceComputer = candidate.SourceComputerName;
            status.LastRestorePromptSourceComputer = string.Empty;
            status.LastRestorePromptedAt = null;
            status.LastError = result.Errors.Count == 0 ? "" : result.Summary + " " + string.Join("; ", result.Errors.Take(3));
            await statusService.SaveAsync(status, cancellationToken);
            logger.LogInformation("{RestoreSummary}", result.Summary);
            if (!automatic || result.Errors.Count > 0)
                MessageBox.Show(result.Summary, "LinkSafe Restore", MessageBoxButtons.OK,
                    result.Errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Missing-file restore failed.");
            status = await statusService.GetAsync(cancellationToken);
            status.LastError = "Restore failed: " + ex.Message;
            await statusService.SaveAsync(status, cancellationToken);
            MessageBox.Show(status.LastError, "LinkSafe Restore", MessageBoxButtons.OK,
                MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        }
    }
}
