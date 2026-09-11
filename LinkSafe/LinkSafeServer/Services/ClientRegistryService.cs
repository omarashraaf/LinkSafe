using Shared.Constants;
using LinkSafeServer.Models;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Services;

public sealed class ClientRegistryService(
    ServerConfigService serverConfigService,
    StorageSecurityService storageSecurityService,
    PathSanitizer pathSanitizer,
    ILogger<ClientRegistryService> logger)
{
    private readonly AsyncMutationLock _mutations = new();
    private readonly JsonFileStore<List<RegisteredClientRecord>> _clientsStore = new(LinkSafeDefaults.RegisteredClientsPath);
    private readonly JsonFileStore<Dictionary<string, string>> _userMapStore = new(LinkSafeDefaults.UserFolderMapPath);

    public Task<List<RegisteredClientRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _clientsStore.LoadAsync(() => [], cancellationToken);

    public async Task<RegisteredClientRecord?> GetAsync(string domainUser, string computerName, CancellationToken cancellationToken = default)
    {
        var clients = await GetAllAsync(cancellationToken);
        return clients.FirstOrDefault(client =>
            client.DomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase) &&
            client.ComputerName.Equals(computerName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<RegistrationResponse> RegisterAsync(RegistrationRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await _mutations.EnterAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.DomainUser) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.ComputerName))
        {
            return new RegistrationResponse { Success = false, Message = "Missing required registration fields." };
        }

        var config = await serverConfigService.GetAsync(cancellationToken);
        await storageSecurityService.EnsureStorageStructureAsync(config, cancellationToken);

        var userFolderName = pathSanitizer.ToUserFolderName(request.DomainUser, request.Username);
        var computerFolderName = pathSanitizer.SanitizeFolderName(request.ComputerName);
        var destinationPath = Path.Combine(config.BackupRoot, userFolderName, computerFolderName);

        Directory.CreateDirectory(destinationPath);

        var clients = await GetAllAsync(cancellationToken);
        var existing = clients.FirstOrDefault(client =>
            client.DomainUser.Equals(request.DomainUser, StringComparison.OrdinalIgnoreCase) &&
            client.ComputerName.Equals(request.ComputerName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new RegisteredClientRecord
            {
                DomainUser = request.DomainUser,
                Username = request.Username,
                Domain = request.Domain,
                ComputerName = request.ComputerName,
                UserFolderName = userFolderName,
                ComputerFolderName = computerFolderName,
                BackupDestinationPath = destinationPath,
                AgentVersion = request.AgentVersion,
                FirstRegisteredAt = DateTimeOffset.UtcNow
            };
            clients.Add(existing);
        }

        existing.Username = request.Username;
        existing.Domain = request.Domain;
        existing.AgentVersion = request.AgentVersion;
        existing.UserFolderName = userFolderName;
        existing.ComputerFolderName = computerFolderName;
        existing.BackupDestinationPath = destinationPath;
        existing.LastSeenAt = DateTimeOffset.UtcNow;
        if (request.UsesLocalFolderSelection.HasValue)
        {
            // Informational only: a client report must never mutate the administrator's policy.
            existing.UsesLocalFolderSelection = request.UsesLocalFolderSelection;
            existing.ReportedBackupFolders = (request.ReportedBackupFolders ?? []).Where(LinkSafeDefaults.KnownFolders.Contains).Distinct().ToList();
            existing.ReportedCustomFolders = (request.ReportedCustomFolders ?? [])
                .Where(path => BackupSourcePaths.TryNormalize(path, out _)).Take(100).ToList();
        }
        if (request.AvailableDrives is not null)
        {
            existing.AvailableDrives = request.AvailableDrives
                .Where(path => BackupSourcePaths.TryNormalize(path, out var normalized) && normalized.Length == 3)
                .Select(path => path.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        }

        await _clientsStore.SaveAsync(clients.OrderBy(item => item.Username).ThenBy(item => item.ComputerName).ToList(), cancellationToken);

        var userMap = await _userMapStore.LoadAsync(() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), cancellationToken);
        userMap[request.DomainUser] = userFolderName;
        await _userMapStore.SaveAsync(userMap, cancellationToken);

        logger.LogInformation("Registered client {DomainUser} on {ComputerName}.", request.DomainUser, request.ComputerName);

        return new RegistrationResponse
        {
            Success = true,
            Message = "Client registered successfully.",
            UserFolderName = userFolderName,
            ComputerFolderName = computerFolderName,
            BackupDestinationId = $"{userFolderName}/{computerFolderName}",
            Policy = serverConfigService.ToClientPolicy(config, existing)
        };
    }

    public async Task<OperationResult> UpdateHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await _mutations.EnterAsync(cancellationToken);
        var clients = await GetAllAsync(cancellationToken);
        var client = clients.FirstOrDefault(item =>
            item.DomainUser.Equals(request.DomainUser, StringComparison.OrdinalIgnoreCase) &&
            item.ComputerName.Equals(request.ComputerName, StringComparison.OrdinalIgnoreCase));

        if (client is null)
        {
            return OperationResult.Fail("Client not found.");
        }

        client.LastSeenAt = DateTimeOffset.UtcNow;
        client.LastBackupTime = request.LastBackupTime;
        client.LastBackupStatus = request.LastBackupStatus;
        client.LastError = request.LastError;
        await _clientsStore.SaveAsync(clients, cancellationToken);
        return OperationResult.Ok("Heartbeat saved.");
    }

    public async Task UpdateBackupStatusAsync(
        string domainUser,
        string computerName,
        DateTimeOffset? lastBackupTime,
        string status,
        string error,
        CancellationToken cancellationToken = default)
    {
        using var mutation = await _mutations.EnterAsync(cancellationToken);
        var clients = await GetAllAsync(cancellationToken);
        var client = clients.FirstOrDefault(item =>
            item.DomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase) &&
            item.ComputerName.Equals(computerName, StringComparison.OrdinalIgnoreCase));

        if (client is null)
        {
            return;
        }

        client.LastSeenAt = DateTimeOffset.UtcNow;
        client.LastBackupTime = lastBackupTime;
        client.LastBackupStatus = status;
        client.LastError = error;
        await _clientsStore.SaveAsync(clients, cancellationToken);
    }

    public async Task DisableAsync(string domainUser, string computerName, bool disabled, CancellationToken cancellationToken = default)
    {
        using var mutation = await _mutations.EnterAsync(cancellationToken);
        var clients = await GetAllAsync(cancellationToken);
        var client = clients.FirstOrDefault(item =>
            item.DomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase) &&
            item.ComputerName.Equals(computerName, StringComparison.OrdinalIgnoreCase));

        if (client is null)
        {
            return;
        }

        client.IsDisabled = disabled;
        await _clientsStore.SaveAsync(clients, cancellationToken);
    }

    public async Task RemoveAsync(string domainUser, string computerName, CancellationToken cancellationToken = default)
    {
        using var mutation = await _mutations.EnterAsync(cancellationToken);
        var clients = await GetAllAsync(cancellationToken);
        clients.RemoveAll(item =>
            item.DomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase) &&
            item.ComputerName.Equals(computerName, StringComparison.OrdinalIgnoreCase));
        await _clientsStore.SaveAsync(clients, cancellationToken);
    }

    public async Task SaveClientPolicyAsync(
        string domainUser,
        string computerName,
        List<string> backupFolders,
        List<string> customFolders,
        CancellationToken cancellationToken = default,
        bool useServerDefaults = false)
    {
        using var mutation = await _mutations.EnterAsync(cancellationToken);
        var clients = await GetAllAsync(cancellationToken);
        var client = clients.FirstOrDefault(item =>
            item.DomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase) &&
            item.ComputerName.Equals(computerName, StringComparison.OrdinalIgnoreCase));

        if (client is null)
        {
            return;
        }

        client.BackupFoldersOverride = useServerDefaults ? [] : backupFolders;
        client.CustomFoldersOverride = useServerDefaults ? [] : customFolders;
        client.HasBackupPolicyOverride = !useServerDefaults;
        await _clientsStore.SaveAsync(clients, cancellationToken);
    }

    public async Task<int> ApplyBulkAsync(List<string> keys, string action, List<string> folders, List<string> paths, CancellationToken token)
    {
        if (action is not ("Settings" or "Defaults" or "Enable" or "Disable" or "Remove")) throw new ArgumentException("Unknown registry action.");
        if (action == "Settings") (folders, paths) = BulkClientSelection.ValidatePolicy(folders, paths);
        using var mutation = await _mutations.EnterAsync(token);
        var clients = await GetAllAsync(token);
        // Resolve again under the same lock as registration and heartbeats, then save once.
        var selected = BulkClientSelection.Resolve(clients, keys);
        foreach (var client in selected)
        {
            if (action == "Remove") clients.Remove(client);
            else if (action is "Enable" or "Disable") client.IsDisabled = action == "Disable";
            else
            {
                client.HasBackupPolicyOverride = action == "Settings";
                client.BackupFoldersOverride = action == "Settings" ? [.. folders] : [];
                client.CustomFoldersOverride = action == "Settings" ? [.. paths] : [];
            }
        }
        await _clientsStore.SaveAsync(clients, token);
        logger.LogInformation("Admin bulk {Action} applied to {Count} registered clients. Backup files were not changed.", action, selected.Count);
        return selected.Count;
    }
}
