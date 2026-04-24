using System.Collections.Concurrent;
using Shared.Constants;
using Shared.Models;

namespace LinkSafeClientAgent.Services;

public sealed class BackupCoordinator(
    AgentConfigService configService,
    AgentStatusService statusService,
    QueueStore queueStore,
    ClientIdentityService identityService,
    ClientApiService apiService,
    RoamingRestorePromptService roamingRestorePromptService,
    BackupPackageBuilder packageBuilder,
    BackupStateService backupStateService,
    FolderMappingService folderMappingService,
    ILogger<BackupCoordinator> logger)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromMinutes(10);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LinkSafeDefaults.ClientQueueDirectory);
        Directory.CreateDirectory(LinkSafeDefaults.ClientLogsDirectory);

        var status = await statusService.GetAsync(cancellationToken);
        status.AgentEnabled = true;
        status.LastRun = DateTimeOffset.UtcNow;
        await statusService.SaveAsync(status, cancellationToken);

        var config = await EnsureRegistrationAndPolicyAsync(cancellationToken);
        await roamingRestorePromptService.OfferRestoreIfNeededAsync(config, cancellationToken);
        StartWatchers(config);

        await RetryQueuedPackagesAsync(config, cancellationToken);
        await RunFullBackupInternalAsync(config, cancellationToken);

        var lastFullBackup = DateTimeOffset.UtcNow;
        var lastHeartbeat = DateTimeOffset.MinValue;
        var lastPolicySync = DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            config = await configService.GetAsync(cancellationToken);

            await ProcessChangedFilesAsync(config, cancellationToken);
            await RetryQueuedPackagesAsync(config, cancellationToken);

            if (DateTimeOffset.UtcNow - lastFullBackup >= TimeSpan.FromMinutes(Math.Max(1, config.BackupIntervalMinutes)))
            {
                await RunFullBackupInternalAsync(config, cancellationToken);
                lastFullBackup = DateTimeOffset.UtcNow;
            }

            if (DateTimeOffset.UtcNow - lastPolicySync >= TimeSpan.FromMinutes(2))
            {
                config = await EnsureRegistrationAndPolicyAsync(cancellationToken);
                StartWatchers(config);
                lastPolicySync = DateTimeOffset.UtcNow;
            }

            if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromMinutes(5))
            {
                await SendHeartbeatAsync(config, cancellationToken);
                lastHeartbeat = DateTimeOffset.UtcNow;
            }

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    public async Task RunImmediateFullBackupAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await UpdateOperationStatusAsync(
                "Checking",
                "Checking for changed files to back up now...",
                startedAt,
                finishedAt: null,
                cancellationToken);

            var config = await EnsureRegistrationAndPolicyAsync(cancellationToken);
            var status = await statusService.GetAsync(cancellationToken);
            var pendingFiles = _changedFiles.Keys.ToList();
            var stablePendingFiles = new List<string>();
            var queuedRepairPackage = await GetLatestManifestAwareQueuedPackageAsync(cancellationToken);

            if (queuedRepairPackage is not null)
            {
                await UpdateOperationStatusAsync(
                    "Uploading",
                    "Uploading queued repair backup package...",
                    startedAt,
                    finishedAt: null,
                    cancellationToken);
                await TryUploadSinglePackageAsync(config, queuedRepairPackage, cancellationToken);
                status = await statusService.GetAsync(cancellationToken);
                if (string.Equals(status.LastBackupStatus, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    await UpdateOperationStatusAsync(
                        "Success",
                        "Queued repair backup uploaded successfully.",
                        startedAt,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                    return;
                }
            }

            await CleanupLegacyQueueAsync(cancellationToken);

            foreach (var filePath in pendingFiles)
            {
                if (await IsStableAsync(filePath, 1, cancellationToken))
                {
                    stablePendingFiles.Add(filePath);
                }
            }

            await UpdateOperationStatusAsync(
                "Packaging",
                "Packaging new and changed files...",
                startedAt,
                finishedAt: null,
                cancellationToken);
            var package = await packageBuilder.CreateSmartBackupAsync(
                config,
                status.LastSuccessfulBackup,
                await backupStateService.GetSnapshotAsync(cancellationToken),
                stablePendingFiles,
                cancellationToken);

            if (package is null)
            {
                await UpdateOperationStatusAsync(
                    "NoChanges",
                    "No new or changed files were found.",
                    startedAt,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                return;
            }

            foreach (var filePath in stablePendingFiles)
            {
                _changedFiles.TryRemove(filePath, out _);
            }

            await EnqueueAndUploadAsync(config, package, retryImmediately: false, cancellationToken);
            await UpdateOperationStatusAsync(
                "Uploading",
                "Uploading backup package to server...",
                startedAt,
                finishedAt: null,
                cancellationToken);
            await TryUploadSinglePackageAsync(config, package, cancellationToken);

            status = await statusService.GetAsync(cancellationToken);
            if (string.Equals(status.LastBackupStatus, "Success", StringComparison.OrdinalIgnoreCase) &&
                status.LastSuccessfulBackup is not null &&
                status.LastSuccessfulBackup >= startedAt.AddSeconds(-5))
            {
                await UpdateOperationStatusAsync(
                    "Success",
                    "Backup completed successfully.",
                    startedAt,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                return;
            }

            await UpdateOperationStatusAsync(
                "Queued",
                "Backup package was created and queued for retry.",
                startedAt,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run backup now failed.");
            await UpdateOperationStatusAsync(
                "Failed",
                $"Backup failed: {ex.Message}",
                startedAt,
                DateTimeOffset.UtcNow,
                cancellationToken);
            throw;
        }
    }

    private async Task<ClientAgentConfig> EnsureRegistrationAndPolicyAsync(CancellationToken cancellationToken)
    {
        var config = await configService.GetAsync(cancellationToken);
        var identity = identityService.GetCurrent();
        var status = await statusService.GetAsync(cancellationToken);
        try
        {
            var health = await apiService.HealthAsync(config.ServerUrl, cancellationToken);
            status.ServerReachable = health is not null;

            var registration = await apiService.RegisterAsync(config, identity, cancellationToken);
            if (registration?.Success == true)
            {
                config.Registered = true;
                config.UserFolderName = registration.UserFolderName;
                config.ComputerFolderName = registration.ComputerFolderName;
                config.BackupDestinationId = registration.BackupDestinationId;
                ApplyPolicy(config, registration.Policy);
            }

            var policy = await apiService.GetPolicyAsync(config, cancellationToken);
            if (policy is not null)
            {
                ApplyPolicy(config, policy);
            }

            status.LastError = string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync client registration or policy.");
            status.ServerReachable = false;
            status.LastError = ex.Message;
        }

        await configService.SaveAsync(config, cancellationToken);
        status.Registered = config.Registered;
        await statusService.SaveAsync(status, cancellationToken);
        return config;
    }

    private async Task RunFullBackupInternalAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var status = await statusService.GetAsync(cancellationToken);
        var package = await packageBuilder.CreateSmartBackupAsync(
            config,
            status.LastSuccessfulBackup,
            await backupStateService.GetSnapshotAsync(cancellationToken),
            prioritizedFiles: [],
            cancellationToken);
        if (package is null)
        {
            status.LastRun = DateTimeOffset.UtcNow;
            status.LastBackupStatus = "NoChanges";
            status.LastOperationStatus = "NoChanges";
            status.LastOperationMessage = "No new or changed files were found.";
            status.LastOperationFinishedAt = DateTimeOffset.UtcNow;
            await statusService.SaveAsync(status, cancellationToken);
            return;
        }

        await EnqueueAndUploadAsync(config, package, retryImmediately: true, cancellationToken);

        status = await statusService.GetAsync(cancellationToken);
        status.LastFullBackup = DateTimeOffset.UtcNow;
        status.LastRun = DateTimeOffset.UtcNow;
        await statusService.SaveAsync(status, cancellationToken);
    }

    private async Task ProcessChangedFilesAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var dueFiles = _changedFiles
            .Where(pair => DateTimeOffset.UtcNow - pair.Value >= TimeSpan.FromSeconds(Math.Max(5, config.DelayBeforeCopySeconds)))
            .Select(pair => pair.Key)
            .ToList();

        if (dueFiles.Count == 0)
        {
            return;
        }

        foreach (var file in dueFiles)
        {
            _changedFiles.TryRemove(file, out _);
        }

        var stableFiles = new List<string>();
        foreach (var filePath in dueFiles)
        {
            if (await IsStableAsync(filePath, config.StableFileCheckSeconds, cancellationToken))
            {
                stableFiles.Add(filePath);
            }
        }

        var package = await packageBuilder.CreateIncrementalBackupAsync(config, stableFiles, cancellationToken);
        if (package is not null)
        {
            await EnqueueAndUploadAsync(config, package, retryImmediately: true, cancellationToken);
        }
    }

    private async Task EnqueueAndUploadAsync(
        ClientAgentConfig config,
        QueuePackageRecord package,
        bool retryImmediately,
        CancellationToken cancellationToken)
    {
        var queue = await queueStore.GetAsync(cancellationToken);
        if (queue.All(item => !item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            queue.Add(package);
            await queueStore.SaveAsync(queue, cancellationToken);
        }

        if (retryImmediately)
        {
            await RetryQueuedPackagesAsync(config, cancellationToken);
        }
    }

    private async Task RetryQueuedPackagesAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var queue = await queueStore.GetAsync(cancellationToken);
        var backupState = await backupStateService.GetSnapshotAsync(cancellationToken);
        var status = await statusService.GetAsync(cancellationToken);
        var changed = false;

        foreach (var package in queue.ToList())
        {
            if (!File.Exists(package.FilePath))
            {
                queue.Remove(package);
                changed = true;
                continue;
            }

            if (ShouldDiscardLegacyPackage(package, backupState, status.LastSuccessfulBackup))
            {
                TryDeletePackage(package.FilePath);
                queue.Remove(package);
                changed = true;
                continue;
            }

            if (!IsRetryDue(package))
            {
                continue;
            }

            try
            {
                if (await TryUploadPackageAsync(config, package, cancellationToken))
                {
                    queue.Remove(package);
                    changed = true;
                }
                else
                {
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Upload retry failed for {PackagePath}.", package.FilePath);
                await MarkPackageQueuedAsync(package, ex.Message, cancellationToken);
                changed = true;
            }
        }

        if (changed)
        {
            await queueStore.SaveAsync(queue, cancellationToken);
        }
    }

    private async Task TryUploadSinglePackageAsync(
        ClientAgentConfig config,
        QueuePackageRecord package,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await TryUploadPackageAsync(config, package, cancellationToken))
            {
                var currentQueue = await queueStore.GetAsync(cancellationToken);
                var currentRecord = currentQueue.FirstOrDefault(item => item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
                if (currentRecord is not null)
                {
                    currentRecord.LastAttemptAt = package.LastAttemptAt;
                    currentRecord.RetryCount = package.RetryCount;
                    currentRecord.LastError = package.LastError;
                }

                await queueStore.SaveAsync(currentQueue, cancellationToken);
                return;
            }

            var queue = await queueStore.GetAsync(cancellationToken);
            var matchingRecord = queue.FirstOrDefault(item => item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
            if (matchingRecord is not null)
            {
                queue.Remove(matchingRecord);
                await queueStore.SaveAsync(queue, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Manual upload failed for {PackagePath}.", package.FilePath);
            await MarkPackageQueuedAsync(package, ex.Message, cancellationToken);
            await SavePackageStateInQueueAsync(package, cancellationToken);
        }
    }

    private async Task<bool> TryUploadPackageAsync(
        ClientAgentConfig config,
        QueuePackageRecord package,
        CancellationToken cancellationToken)
    {
        package.LastAttemptAt = DateTimeOffset.UtcNow;
        package.RetryCount++;
        var response = await apiService.UploadAsync(config, package.FilePath, package.MetadataJson, cancellationToken);
        if (response?.Success != true)
        {
            await MarkPackageQueuedAsync(package, response?.Message ?? "Upload failed.", cancellationToken);
            return false;
        }

        File.Delete(package.FilePath);

        var status = await statusService.GetAsync(cancellationToken);
        status.LastSuccessfulBackup = DateTimeOffset.UtcNow;
        status.LastBackupStatus = "Success";
        status.LastError = string.Empty;
        status.LastOperationStatus = "Success";
        status.LastOperationMessage = "Backup completed successfully.";
        status.LastOperationFinishedAt = DateTimeOffset.UtcNow;
        await statusService.SaveAsync(status, cancellationToken);
        await backupStateService.MarkConfirmedAsync(package.IncludedFiles, cancellationToken);
        return true;
    }

    private async Task MarkPackageQueuedAsync(
        QueuePackageRecord package,
        string message,
        CancellationToken cancellationToken)
    {
        package.LastError = message;

        var status = await statusService.GetAsync(cancellationToken);
        status.LastBackupStatus = "Queued";
        status.LastError = message;
        status.LastOperationStatus = "Queued";
        status.LastOperationMessage = "Backup upload failed and will retry automatically.";
        status.LastOperationFinishedAt = DateTimeOffset.UtcNow;
        await statusService.SaveAsync(status, cancellationToken);
    }

    private async Task<QueuePackageRecord?> GetLatestManifestAwareQueuedPackageAsync(CancellationToken cancellationToken)
    {
        var backupState = await backupStateService.GetSnapshotAsync(cancellationToken);
        if (backupState.Count > 0)
        {
            return null;
        }

        var queue = await queueStore.GetAsync(cancellationToken);
        return queue
            .Where(package => package.IncludedFiles.Count > 0 && File.Exists(package.FilePath))
            .OrderByDescending(package => package.CreatedAt)
            .FirstOrDefault();
    }

    private async Task SavePackageStateInQueueAsync(QueuePackageRecord package, CancellationToken cancellationToken)
    {
        var queue = await queueStore.GetAsync(cancellationToken);
        var matchingRecord = queue.FirstOrDefault(item => item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
        if (matchingRecord is null)
        {
            return;
        }

        matchingRecord.LastAttemptAt = package.LastAttemptAt;
        matchingRecord.RetryCount = package.RetryCount;
        matchingRecord.LastError = package.LastError;
        await queueStore.SaveAsync(queue, cancellationToken);
    }

    private async Task CleanupLegacyQueueAsync(CancellationToken cancellationToken)
    {
        var backupState = await backupStateService.GetSnapshotAsync(cancellationToken);
        if (backupState.Count == 0)
        {
            return;
        }

        var status = await statusService.GetAsync(cancellationToken);
        var queue = await queueStore.GetAsync(cancellationToken);
        var changed = false;

        foreach (var package in queue.ToList())
        {
            if (!ShouldDiscardLegacyPackage(package, backupState, status.LastSuccessfulBackup))
            {
                continue;
            }

            TryDeletePackage(package.FilePath);
            queue.Remove(package);
            changed = true;
        }

        if (changed)
        {
            await queueStore.SaveAsync(queue, cancellationToken);
        }
    }

    private async Task SendHeartbeatAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var identity = identityService.GetCurrent();
        var status = await statusService.GetAsync(cancellationToken);

        try
        {
            await apiService.SendHeartbeatAsync(
                config,
                new HeartbeatRequest
                {
                    DomainUser = identity.DomainUser,
                    Username = identity.Username,
                    ComputerName = identity.ComputerName,
                    LastBackupTime = status.LastSuccessfulBackup,
                    LastBackupStatus = status.LastBackupStatus,
                    LastError = status.LastError
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Heartbeat failed.");
        }
    }

    private static bool IsRetryDue(QueuePackageRecord package)
    {
        if (package.LastAttemptAt is null)
        {
            return true;
        }

        var retryDelay = TimeSpan.FromMinutes(Math.Min(60, Math.Max(10, package.RetryCount * 10)));
        return DateTimeOffset.UtcNow - package.LastAttemptAt >= retryDelay && retryDelay >= MinimumRetryDelay;
    }

    private static bool ShouldDiscardLegacyPackage(
        QueuePackageRecord package,
        IReadOnlyDictionary<string, BackupFileStateEntry> backupState,
        DateTimeOffset? lastSuccessfulBackup)
    {
        return backupState.Count > 0 &&
            package.IncludedFiles.Count == 0 &&
            lastSuccessfulBackup is not null &&
            package.CreatedAt < lastSuccessfulBackup;
    }

    private void TryDeletePackage(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete stale queued package {FilePath}.", filePath);
        }
    }

    private void StartWatchers(ClientAgentConfig config)
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();

        var folders = folderMappingService.GetBackupFolders(config);
        foreach (var (_, folderPath) in folders)
        {
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            watcher.Created += OnFileChanged;
            watcher.Changed += OnFileChanged;
            watcher.Renamed += (_, args) => _changedFiles[args.FullPath] = DateTimeOffset.UtcNow;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.FullPath))
        {
            _changedFiles[args.FullPath] = DateTimeOffset.UtcNow;
        }
    }

    private static void ApplyPolicy(ClientAgentConfig config, ClientPolicyResponse policy)
    {
        config.BackupFolders = [.. policy.BackupFolders];
        config.CustomFolders = [.. policy.CustomFolders];
        config.ExcludeDirectories = [.. policy.ExcludeDirectories];
        config.ExcludeFiles = [.. policy.ExcludeFiles];
        config.BackupIntervalMinutes = policy.BackupIntervalMinutes;
        config.DelayBeforeCopySeconds = policy.DelayBeforeCopySeconds;
        config.StableFileCheckSeconds = policy.StableFileCheckSeconds;
        config.MaxFileSizeMB = policy.MaxFileSizeMB;
        config.MaxUploadSizeMB = policy.MaxUploadSizeMB;
    }

    private static async Task<bool> IsStableAsync(string filePath, int stableSeconds, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        var firstLength = new FileInfo(filePath).Length;
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, stableSeconds)), cancellationToken);
        if (!File.Exists(filePath))
        {
            return false;
        }

        var secondLength = new FileInfo(filePath).Length;
        return firstLength == secondLength;
    }

    private async Task UpdateOperationStatusAsync(
        string operationStatus,
        string operationMessage,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,
        CancellationToken cancellationToken)
    {
        var status = await statusService.GetAsync(cancellationToken);
        status.LastRun = DateTimeOffset.UtcNow;
        status.LastOperationStartedAt = startedAt;
        status.LastOperationFinishedAt = finishedAt;
        status.LastOperationStatus = operationStatus;
        status.LastOperationMessage = operationMessage;
        if (string.Equals(operationStatus, "Running", StringComparison.OrdinalIgnoreCase))
        {
            status.LastError = string.Empty;
            status.LastBackupStatus = "Running";
        }
        else if (string.Equals(operationStatus, "Checking", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(operationStatus, "Packaging", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(operationStatus, "Uploading", StringComparison.OrdinalIgnoreCase))
        {
            status.LastError = string.Empty;
            status.LastBackupStatus = "Running";
        }
        else if (string.Equals(operationStatus, "NoChanges", StringComparison.OrdinalIgnoreCase))
        {
            status.LastBackupStatus = "NoChanges";
            status.LastError = string.Empty;
        }
        else if (string.Equals(operationStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            status.LastBackupStatus = "Failed";
            status.LastError = operationMessage;
        }

        await statusService.SaveAsync(status, cancellationToken);
    }
}
