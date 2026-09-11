using Shared.Models;

namespace Shared.Storage;

public sealed class BackupStopControl(string signalPath)
{
    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        try { return (await File.ReadAllTextAsync(signalPath, cancellationToken)).Trim(); }
        catch (FileNotFoundException) { return string.Empty; }
        catch (DirectoryNotFoundException) { return string.Empty; }
    }

    public async Task<string> RunAsync(Func<CancellationToken, Task> work, CancellationToken lifetime)
    {
        var stopId = await ReadAsync(lifetime);
        if (!string.IsNullOrEmpty(stopId)) return stopId;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var monitorLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var monitor = Task.Run(async () =>
        {
            while (!monitorLifetime.IsCancellationRequested)
            {
                var request = await ReadAsync(monitorLifetime.Token);
                if (!string.IsNullOrEmpty(request))
                {
                    stopId = request;
                    operation.Cancel();
                    return;
                }
                await Task.Delay(250, monitorLifetime.Token);
            }
        }, CancellationToken.None);
        try { await work(operation.Token); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && operation.IsCancellationRequested) { }
        finally
        {
            monitorLifetime.Cancel();
            try { await monitor; }
            catch (OperationCanceledException) when (monitorLifetime.IsCancellationRequested) { }
        }
        lifetime.ThrowIfCancellationRequested();
        return string.IsNullOrEmpty(stopId) ? await ReadAsync(lifetime) : stopId;
    }

    public static void MarkStopped(StatusFileModel status, string stopId, string pendingBackupId)
    {
        var backupId = string.IsNullOrWhiteSpace(pendingBackupId) ? status.LastOperationRequestId : pendingBackupId;
        status.LastStopRequestId = stopId;
        status.LastOperationStatus = "Stopped";
        status.LastBackupStatus = "Stopped";
        status.LastOperationMessage = "Backup stopped. Queued files are kept. Click Back up now to resume backups.";
        status.LastOperationFinishedAt = DateTimeOffset.UtcNow;
        status.BackupProgressPercent = null;
        status.LastError = string.Empty;
        if (!string.IsNullOrWhiteSpace(backupId))
        {
            status.CompletedBackupRequestId = backupId;
            status.CompletedBackupRequestStatus = "Stopped";
            status.CompletedBackupRequestMessage = status.LastOperationMessage;
        }
    }
}
