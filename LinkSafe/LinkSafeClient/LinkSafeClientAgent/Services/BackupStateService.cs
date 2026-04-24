using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class BackupStateService
{
    private readonly JsonFileStore<BackupStateFile> _store = new(LinkSafeDefaults.ClientBackupStatePath);

    public async Task<IReadOnlyDictionary<string, BackupFileStateEntry>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var state = await _store.LoadAsync(() => new BackupStateFile(), cancellationToken);
        return state.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.SourcePath))
            .GroupBy(file => file.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(file => file.ConfirmedAt).First(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task MarkConfirmedAsync(IReadOnlyCollection<BackupFileStateEntry> confirmedFiles, CancellationToken cancellationToken)
    {
        if (confirmedFiles.Count == 0)
        {
            return;
        }

        var state = await _store.LoadAsync(() => new BackupStateFile(), cancellationToken);
        var byPath = state.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.SourcePath))
            .GroupBy(file => file.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(file => file.ConfirmedAt).First(), StringComparer.OrdinalIgnoreCase);

        foreach (var file in confirmedFiles)
        {
            byPath[file.SourcePath] = file;
        }

        state.Files = byPath.Values.OrderBy(file => file.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
        await _store.SaveAsync(state, cancellationToken);
    }
}
