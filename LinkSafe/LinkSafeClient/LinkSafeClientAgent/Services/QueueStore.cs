using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class QueueStore
{
    private readonly JsonFileStore<List<QueuePackageRecord>> _store = new(LinkSafeDefaults.ClientQueuePath);

    public Task<List<QueuePackageRecord>> GetAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(() => [], cancellationToken);

    public Task SaveAsync(List<QueuePackageRecord> queue, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(queue, cancellationToken);
}
