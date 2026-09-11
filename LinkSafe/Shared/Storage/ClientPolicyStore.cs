using System.Text.Json;
using Shared.Constants;
using Shared.Models;

namespace Shared.Storage;

public sealed class LocalBackupSelection
{
    public string ServerUrl { get; set; } = "";
    public bool OverrideServerFolders { get; set; }
    public List<string> BackupFolders { get; set; } = [];
    public List<string> CustomFolders { get; set; } = [];
}

public sealed class CachedClientPolicy
{
    public string ServerUrl { get; set; } = "";
    public DateTimeOffset? SyncedAt { get; set; }
    public ClientPolicyResponse? Policy { get; set; }
}

// Separate files have separate owners: the agent writes policy, the UI writes selection.
public sealed class ClientPolicyStore(string? directory = null)
{
    private readonly JsonFileStore<CachedClientPolicy> _policy = new(Path.Combine(
        directory ?? Path.GetDirectoryName(LinkSafeDefaults.ClientConfigPath)!, "server-policy.json"));
    private readonly JsonFileStore<LocalBackupSelection> _selection = new(Path.Combine(
        directory ?? Path.GetDirectoryName(LinkSafeDefaults.ClientConfigPath)!, "local-backup-selection.json"));

    public Task<CachedClientPolicy> GetPolicyAsync(CancellationToken token = default) => _policy.ReadAsync(() => new(), token);
    public Task<LocalBackupSelection> GetSelectionAsync(CancellationToken token = default) => _selection.ReadAsync(() => new(), token);
    public Task SavePolicyAsync(string serverUrl, ClientPolicyResponse policy, CancellationToken token = default) =>
        _policy.SaveAsync(new() { ServerUrl = serverUrl, Policy = policy, SyncedAt = DateTimeOffset.UtcNow }, token);

    public Task SaveSelectionAsync(LocalBackupSelection selection, CancellationToken token = default)
    {
        if (selection.BackupFolders.Any(folder => !LinkSafeDefaults.KnownFolders.Contains(folder)))
            throw new ArgumentException("Choose a supported user folder.");
        selection.BackupFolders = selection.BackupFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        selection.CustomFolders = selection.CustomFolders.Select(path =>
            BackupSourcePaths.TryNormalize(path, out var normalized) ? normalized :
            throw new ArgumentException("Use a full local folder path, for example D:\\Projects.")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return _selection.SaveAsync(selection, token);
    }

    public async Task<ClientAgentConfig> ApplyAsync(ClientAgentConfig config, CancellationToken token = default)
    {
        var cached = await GetPolicyAsync(token);
        if (SameServer(config.ServerUrl, cached.ServerUrl) && cached.Policy is not null)
            ApplyServerPolicy(config, cached.Policy);
        var local = await GetSelectionAsync(token);
        config.UsesLocalFolderSelection = SameServer(config.ServerUrl, local.ServerUrl) && local.OverrideServerFolders;
        if (config.UsesLocalFolderSelection)
        {
            config.BackupFolders = [.. local.BackupFolders];
            config.CustomFolders = [.. local.CustomFolders];
        }
        return config;
    }

    public static bool SameServer(string left, string right) =>
        left.TrimEnd('/').Equals(right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public static void ApplyServerPolicy(ClientAgentConfig config, ClientPolicyResponse policy)
    {
        config.BackupFolders = [.. policy.BackupFolders];
        config.CustomFolders = [.. policy.CustomFolders];
        config.ProtectedSourcePaths = [.. policy.ProtectedSourcePaths];
        config.ExcludeDirectories = [.. policy.ExcludeDirectories];
        config.ExcludeFiles = [.. policy.ExcludeFiles];
        config.BackupIntervalMinutes = policy.BackupIntervalMinutes;
        config.DelayBeforeCopySeconds = policy.DelayBeforeCopySeconds;
        config.StableFileCheckSeconds = policy.StableFileCheckSeconds;
        config.MaxFileSizeMB = policy.MaxFileSizeMB;
        config.MaxUploadSizeMB = policy.MaxUploadSizeMB;
    }

    public static string ScanKey(ClientAgentConfig config) => JsonSerializer.Serialize(new
    {
        config.ServerUrl,
        Folders = config.BackupFolders.Order(StringComparer.OrdinalIgnoreCase),
        Custom = config.CustomFolders.Order(StringComparer.OrdinalIgnoreCase),
        Protected = config.ProtectedSourcePaths.Order(StringComparer.OrdinalIgnoreCase),
        Directories = config.ExcludeDirectories.Order(StringComparer.OrdinalIgnoreCase),
        Files = config.ExcludeFiles.Order(StringComparer.OrdinalIgnoreCase),
        config.MaxFileSizeMB
    });
}
