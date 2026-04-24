using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class AgentConfigService
{
    private readonly JsonFileStore<ClientAgentConfig> _store = new(LinkSafeDefaults.ClientConfigPath);

    public Task<ClientAgentConfig> GetAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(() => new ClientAgentConfig(), cancellationToken);

    public Task SaveAsync(ClientAgentConfig config, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(config, cancellationToken);
}
