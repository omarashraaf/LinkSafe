using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Services;

public sealed class BackupJobService
{
    private readonly JsonFileStore<List<BackupJobRecord>> _store = new(LinkSafeDefaults.BackupJobsPath);

    public Task<List<BackupJobRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(() => [], cancellationToken);

    public async Task AddOrUpdateAsync(BackupJobRecord job, CancellationToken cancellationToken = default)
    {
        var jobs = await GetAllAsync(cancellationToken);
        var index = jobs.FindIndex(existing => existing.BackupJobId.Equals(job.BackupJobId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            jobs[index] = job;
        }
        else
        {
            jobs.Add(job);
        }

        jobs.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));
        await _store.SaveAsync(jobs, cancellationToken);
    }
}
