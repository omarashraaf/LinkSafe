using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class AgentConfigService(string? directory = null)
{
    private readonly JsonFileStore<ClientAgentConfig> _store = new(directory is null ? LinkSafeDefaults.ClientConfigPath : Path.Combine(directory, "client-agent-config.json"));
    public ClientPolicyStore Policies { get; } = new(directory);

    public async Task<ClientAgentConfig> GetAsync(CancellationToken cancellationToken = default) =>
        await Policies.ApplyAsync(await _store.LoadAsync(() => new ClientAgentConfig(), cancellationToken), cancellationToken);

    public async Task SaveAsync(ClientAgentConfig config, CancellationToken cancellationToken = default)
    {
        var stored = await _store.LoadAsync(() => new ClientAgentConfig(), cancellationToken);
        if (!ClientPolicyStore.SameServer(stored.ServerUrl, config.ServerUrl)) return;
        stored.Registered = config.Registered;
        stored.UserFolderName = config.UserFolderName;
        stored.ComputerFolderName = config.ComputerFolderName;
        stored.BackupDestinationId = config.BackupDestinationId;
        await _store.SaveAsync(stored, cancellationToken);
    }
}
