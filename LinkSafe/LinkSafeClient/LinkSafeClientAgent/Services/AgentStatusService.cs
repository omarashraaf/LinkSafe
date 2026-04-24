using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class AgentStatusService
{
    private readonly JsonFileStore<StatusFileModel> _store = new(LinkSafeDefaults.ClientStatusPath);

    public Task<StatusFileModel> GetAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(() => new StatusFileModel(), cancellationToken);

    public Task SaveAsync(StatusFileModel status, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(status, cancellationToken);
}
