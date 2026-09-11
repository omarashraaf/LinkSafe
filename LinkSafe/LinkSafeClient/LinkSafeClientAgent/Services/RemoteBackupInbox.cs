using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class RemoteBackupInbox(string? path = null)
{
    public sealed class Entry
    {
        public string ServerUrl { get; set; } = "";
        public RemoteBackupRequest? Request { get; set; }
    }
    private readonly JsonFileStore<Entry> _store = new(path ?? Path.Combine(LinkSafeDefaults.ClientDataRoot, "remote-backup-request.json"));
    private readonly AsyncMutationLock _gate = new();

    public async Task<RemoteBackupRequest?> ReadAsync(string server, CancellationToken token)
    {
        var entry = await _store.ReadAsync(() => new(), token);
        return ClientPolicyStore.SameServer(entry.ServerUrl, server) ? entry.Request : null;
    }

    public async Task AcceptAsync(string server, RemoteBackupRequest request, CancellationToken token)
    {
        if (!Guid.TryParse(request.RequestId, out _)) throw new InvalidDataException("Invalid server backup request ID.");
        if ((await ReadAsync(server, token))?.RequestId == request.RequestId) return;
        using var lease = await _gate.EnterAsync(token);
        var previous = await ReadAsync(server, token);
        // Repeated delivery must not reset running or completed work after a lost response.
        if (previous?.RequestId == request.RequestId) return;
        if (previous is { IsTerminal: false }) throw new InvalidOperationException("Another server backup request is pending.");
        request.Status = "Queued";
        request.Message = "Received. Waiting for current backup work to finish.";
        await _store.SaveAsync(new Entry { ServerUrl = server, Request = request }, token);
    }

    public async Task ExecuteAsync(string server, Func<string, CancellationToken, Task<(string Status, string Message)>> execute, CancellationToken token)
    {
        using var lease = await _gate.EnterAsync(token);
        var request = await ReadAsync(server, token);
        if (request is null || request.IsTerminal) return;
        request.Status = "Running";
        request.Message = "Scanning selected folders and verifying existing server backups.";
        var entry = new Entry { ServerUrl = server, Request = request };
        await _store.SaveAsync(entry, token);
        try
        {
            var result = await execute(request.RequestId, token);
            request.Status = result.Status is "Success" or "NoChanges" or "Stopped" ? result.Status : "Failed";
            request.Message = result.Message;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            request.Status = "Stopped";
            request.Message = "Backup was interrupted or stopped. Existing backups and queued packages are retained.";
            throw;
        }
        catch (Exception ex)
        {
            request.Status = "Failed";
            request.Message = ex.Message;
        }
        finally
        {
            // Persist the result even during shutdown; the poller reports it after reconnection.
            await _store.SaveAsync(entry, CancellationToken.None);
        }
    }
}
