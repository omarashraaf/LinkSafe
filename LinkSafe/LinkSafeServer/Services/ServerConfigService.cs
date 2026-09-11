using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Services;

public sealed class ServerConfigService(ILogger<ServerConfigService> logger)
{
    private readonly JsonFileStore<ServerConfig> _store = new(LinkSafeDefaults.ServerConfigPath);

    public async Task<ServerConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        var config = await _store.LoadAsync(CreateDefaultConfig, cancellationToken);
        EnsureDerivedPaths(config);
        return config;
    }

    public async Task SaveAsync(ServerConfig config, CancellationToken cancellationToken = default)
    {
        EnsureDerivedPaths(config);

        await _store.SaveAsync(config, cancellationToken);
        logger.LogInformation("Server configuration saved.");
    }

    public async Task InitializeAdminAsync(string username, string passwordHash, CancellationToken cancellationToken = default)
    {
        var config = await GetAsync(cancellationToken);
        config.AdminUsername = username;
        config.AdminPasswordHash = passwordHash;

        await SaveAsync(config, cancellationToken);
    }

    public ClientPolicyResponse ToClientPolicy(ServerConfig config, RegisteredClientRecord? client = null)
    {
        var hasOverride = client is not null && (client.HasBackupPolicyOverride || client.BackupFoldersOverride.Count > 0 || client.CustomFoldersOverride.Count > 0);
        var backupFolders = hasOverride
            ? client!.BackupFoldersOverride
            : config.BackupFolders;

        var customFolders = hasOverride
            ? client!.CustomFoldersOverride
            : config.CustomFolders;

        return new ClientPolicyResponse
        {
            BackupFolders = [.. backupFolders],
            CustomFolders = [.. customFolders],
            ProtectedSourcePaths = [config.BackupRoot],
            ExcludeDirectories = [.. config.ExcludeDirectories],
            ExcludeFiles = [.. config.ExcludeFiles],
            BackupIntervalMinutes = config.BackupIntervalMinutes,
            DelayBeforeCopySeconds = config.DelayBeforeCopySeconds,
            StableFileCheckSeconds = config.StableFileCheckSeconds,
            MaxFileSizeMB = config.MaxFileSizeMB,
            MaxUploadSizeMB = config.MaxUploadSizeMB,
            IsClientOverride = hasOverride
        };
    }

    private static ServerConfig CreateDefaultConfig()
    {
        var config = new ServerConfig();

        EnsureDerivedPaths(config);
        return config;
    }

    private static void EnsureDerivedPaths(ServerConfig config)
    {
        config.IncomingRoot = Path.Combine(config.BackupRoot, "_Incoming");
        config.QuarantineRoot = Path.Combine(config.BackupRoot, "_Quarantine");
        config.VersionsRoot = Path.Combine(config.BackupRoot, "_Versions");
        config.LogsRoot = Path.Combine(config.BackupRoot, "_Logs");
    }
}
