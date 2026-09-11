using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Services;

public sealed class RemoteBackupService(ClientRegistryService registry, string? dataFile = null)
{
    private readonly JsonFileStore<List<RemoteBackupRequest>> _store = new(dataFile ??
        Path.Combine(Path.GetDirectoryName(LinkSafeDefaults.ServerConfigPath)!, "remote-backup-requests.json"));
    private readonly AsyncMutationLock _gate = new();

    public Task<List<RemoteBackupRequest>> GetAllAsync(CancellationToken token) => _store.ReadAsync(() => [], token);

    private async Task<RegisteredClientRecord> RequireClientAsync(string user, string computer, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(computer))
            throw new ArgumentException("A registered user and computer are required.");
        var client = await registry.GetAsync(user, computer, token);
        if (client is null || client.IsDisabled) throw new InvalidOperationException("Client is missing or disabled.");
        if (!RemoteBackupRequest.Supports(client.AgentVersion))
            throw new InvalidOperationException("Update this computer's agent to 1.0.16 or later to request backups from the server.");
        return client;
    }

    public async Task<RemoteBackupRequest> EnqueueAsync(string user, string computer, CancellationToken token)
    {
        using var lease = await _gate.EnterAsync(token);
        await RequireClientAsync(user, computer, token);
        var items = await GetAllAsync(token);
        var existing = items.FirstOrDefault(x => x.Matches(user, computer));
        if (existing is not null && !existing.IsTerminal) return existing;
        items.RemoveAll(x => x.Matches(user, computer));
        var request = new RemoteBackupRequest { RequestId = Guid.NewGuid().ToString("D"),
            DomainUser = user, ComputerName = computer, RequestedAt = DateTimeOffset.UtcNow };
        items.Add(request);
        await _store.SaveAsync(items, token);
        return request;
    }

    public async Task<RemoteBackupRequest?> PollAsync(RemoteBackupPoll poll, CancellationToken token)
    {
        using var lease = await _gate.EnterAsync(token);
        await RequireClientAsync(poll.DomainUser, poll.ComputerName, token);
        var items = await GetAllAsync(token);
        var request = items.FirstOrDefault(x => x.Matches(poll.DomainUser, poll.ComputerName));
        if (request is null || request.IsTerminal) return null;
        if (request.RequestId == poll.RequestId)
        {
            if (poll.Status is not ("Queued" or "Running" or "Blocked" or "Success" or "NoChanges" or "Failed" or "Stopped"))
                throw new ArgumentException("Unsupported backup request status.");
            request.Status = poll.Status;
            request.Message = (poll.Message ?? "")[..Math.Min(poll.Message?.Length ?? 0, 500)];
            request.UpdatedAt = DateTimeOffset.UtcNow;
            await _store.SaveAsync(items, token);
        }
        return request.IsTerminal ? null : request;
    }
}
