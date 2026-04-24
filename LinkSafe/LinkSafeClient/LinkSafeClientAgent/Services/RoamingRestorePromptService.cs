using System.IO.Compression;
using System.Windows.Forms;
using Shared.Constants;
using Shared.Models;

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
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return;
        }

        var identity = clientIdentityService.GetCurrent();
        var options = await clientApiService.GetRoamingRestoreOptionsAsync(config, identity, cancellationToken);
        var candidate = options?.Candidates
            .OrderByDescending(item => item.LastBackupTime ?? item.LastSeenAt)
            .FirstOrDefault();

        if (candidate is null)
        {
            return;
        }

        var status = await statusService.GetAsync(cancellationToken);
        if (status.LastSuccessfulBackup is not null)
        {
            return;
        }

        if (candidate.SourceComputerName.Equals(status.LastRestoreSourceComputer, StringComparison.OrdinalIgnoreCase) ||
            candidate.SourceComputerName.Equals(status.LastRestorePromptSourceComputer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var backupTime = candidate.LastBackupTime?.ToLocalTime().ToString("g") ?? "an earlier session";
        var message =
            $"LinkSafe found backup data for {identity.DomainUser} from {candidate.SourceComputerName} ({backupTime})." +
            $"{Environment.NewLine}{Environment.NewLine}Do you want to restore that data to this PC now?";

        var result = MessageBox.Show(
            message,
            "LinkSafe Restore",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly);

        status.LastRestorePromptSourceComputer = candidate.SourceComputerName;
        status.LastRestorePromptedAt = DateTimeOffset.UtcNow;
        await statusService.SaveAsync(status, cancellationToken);

        if (result != DialogResult.Yes)
        {
            logger.LogInformation(
                "User declined roaming restore from {SourceComputerName} for {DomainUser}.",
                candidate.SourceComputerName,
                identity.DomainUser);
            return;
        }

        var restoreArchivePath = Path.Combine(
            LinkSafeDefaults.ClientRestoreDirectory,
            $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.zip");

        try
        {
            Directory.CreateDirectory(LinkSafeDefaults.ClientRestoreDirectory);
            await clientApiService.DownloadRestoreArchiveAsync(
                config,
                identity.DomainUser,
                candidate.SourceComputerName,
                restoreArchivePath,
                cancellationToken);

            await RestoreArchiveAsync(restoreArchivePath, cancellationToken);

            status.LastRestoreAt = DateTimeOffset.UtcNow;
            status.LastRestoreSourceComputer = candidate.SourceComputerName;
            status.LastRestorePromptSourceComputer = string.Empty;
            status.LastRestorePromptedAt = null;
            status.LastError = string.Empty;
            await statusService.SaveAsync(status, cancellationToken);

            MessageBox.Show(
                $"LinkSafe restored data from {candidate.SourceComputerName}.",
                "LinkSafe Restore",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roaming restore from {SourceComputerName} failed.", candidate.SourceComputerName);
            status.LastError = $"Restore failed: {ex.Message}";
            await statusService.SaveAsync(status, cancellationToken);

            MessageBox.Show(
                $"LinkSafe could not restore data: {ex.Message}",
                "LinkSafe Restore",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        finally
        {
            TryDeleteFile(restoreArchivePath);
        }
    }

    private async Task RestoreArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var normalizedEntry = entry.FullName.Replace('\\', '/');
            if (normalizedEntry.StartsWith('/') || normalizedEntry.Contains(':') || normalizedEntry.Contains("../", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Restore package contains an unsafe path: {entry.FullName}");
            }

            var segments = normalizedEntry.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                continue;
            }

            var folderName = segments[0];
            var relativePath = segments[1].Replace('/', Path.DirectorySeparatorChar);

            if (!folderMappingService.TryResolveRestoreFolder(folderName, out var restoreRoot))
            {
                logger.LogInformation("Skipping unsupported restore folder {FolderName}.", folderName);
                continue;
            }

            var fullRoot = Path.GetFullPath(restoreRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destinationPath = Path.GetFullPath(Path.Combine(restoreRoot, relativePath));
            if (!destinationPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Restore entry '{entry.FullName}' resolves outside the target folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var entryStream = entry.Open();
            await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await entryStream.CopyToAsync(destinationStream, cancellationToken);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for restore archives.
        }
    }
}
