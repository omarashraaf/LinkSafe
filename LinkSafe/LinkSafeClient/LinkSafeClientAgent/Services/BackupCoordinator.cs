using System.Collections.Concurrent;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class BackupCoordinator(
    AgentConfigService configService,
    AgentStatusService statusService,
    QueueStore queueStore,
    ClientIdentityService identityService,
    ClientApiService apiService,
    BackupPackageBuilder packageBuilder,
    BackupStateService backupStateService,
    FolderMappingService folderMappingService,
    ILogger<BackupCoordinator> logger,
    RemoteBackupInbox? remoteInbox = null)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromMinutes(10);
    private const int MaxPackagesPerBackupRun = 64;
    private string _activeRequestId = string.Empty;
    private string _interruptedRequestId = string.Empty;
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LinkSafeDefaults.ClientQueueDirectory);
        Directory.CreateDirectory(LinkSafeDefaults.ClientLogsDirectory);

        var status = await statusService.GetAsync(cancellationToken);
        status.AgentEnabled = true;
        status.LastRun = DateTimeOffset.UtcNow;
        await statusService.SaveAsync(status, cancellationToken);

        var config = await EnsureRegistrationAndPolicyAsync(cancellationToken);
        StartWatchers(config);
        var scannedPolicy = ClientPolicyStore.ScanKey(config);
        var watchedRoots = WatcherRootsKey(config);

        await RunStoppableBackupAsync(async backupToken =>
        {
            if (File.Exists(LinkSafeDefaults.ClientRunBackupSignalPath))
                await ProcessRequestedBackupAsync(config, backupToken);
            else
            {
                await RetryQueuedPackagesAsync(config, backupToken);
                logger.LogInformation("Starting logon backup scan for new and modified files.");
                try { await RunFullBackupInternalAsync(config, backupToken); }
                catch (Exception ex) when (!backupToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Logon backup check failed; the agent remains available for another attempt.");
                    await UpdateOperationStatusAsync("Failed", $"Backup check failed: {ex.Message}",
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, cancellationToken);
                }
            }
        }, cancellationToken);

        var lastHeartbeat = DateTimeOffset.MinValue;
        var lastPolicyScanAttempt = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            config = await configService.GetAsync(cancellationToken);
            var currentPolicy = ClientPolicyStore.ScanKey(config);
            var currentRoots = WatcherRootsKey(config);
            if (currentRoots != watchedRoots)
            {
                StartWatchers(config);
                watchedRoots = currentRoots;
                // A previously selected drive/folder can become available without a policy edit.
                scannedPolicy = string.Empty;
            }

            await RunStoppableBackupAsync(async backupToken =>
            {
                await ProcessRequestedBackupAsync(config, backupToken);
                if (currentPolicy != scannedPolicy && DateTimeOffset.UtcNow - lastPolicyScanAttempt >= TimeSpan.FromMinutes(2))
                {
                    lastPolicyScanAttempt = DateTimeOffset.UtcNow;
                    logger.LogInformation("Backup selection changed. Scanning selected sources for existing and new files.");
                    try
                    {
                        await RunFullBackupInternalAsync(config, backupToken);
                        scannedPolicy = currentPolicy;
                    }
                    catch (Exception ex) when (!backupToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Selection scan failed; will retry without stopping the agent.");
                        await UpdateOperationStatusAsync("Failed", $"Selection scan failed: {ex.Message}",
                            lastPolicyScanAttempt, DateTimeOffset.UtcNow, cancellationToken);
                    }
                }
                await ProcessChangedFilesAsync(config, backupToken);
                await RetryQueuedPackagesAsync(config, backupToken);
            }, cancellationToken);

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
                        "Checking",
                        "Queued package uploaded. Scanning all selected folders...",
                        startedAt,
                        finishedAt: null,
                        cancellationToken);
                    // A queued upload is not a scan of the currently selected folders.
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

            var result = await RunBackupPackagesAsync(
                config,
                startedAt,
                stablePendingFiles,
                manualRun: true,
                cancellationToken);

            if (result == BackupExecutionResult.NoChanges)
            {
                await UpdateOperationStatusAsync(
                    "NoChanges",
                    "No new or changed files or folders were found.",
                    startedAt,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                return;
            }

            if (result == BackupExecutionResult.Success)
            {
                foreach (var filePath in stablePendingFiles)
                {
                    _changedFiles.TryRemove(filePath, out _);
                }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
                await configService.Policies.SavePolicyAsync(config.ServerUrl, registration.Policy, cancellationToken);
            }

            var policy = await apiService.GetPolicyAsync(config, cancellationToken);
            if (policy is not null)
            {
                await configService.Policies.SavePolicyAsync(config.ServerUrl, policy, cancellationToken);
            }

            status.LastError = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync client registration or policy.");
            status.ServerReachable = false;
            status.LastError = ex.Message;
        }

        await configService.SaveAsync(config, cancellationToken);
        status.Registered = config.Registered;
        await statusService.SaveAsync(status, cancellationToken);
        return await configService.GetAsync(cancellationToken);
    }

    private async Task RunFullBackupInternalAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var result = await RunBackupPackagesAsync(
            config,
            startedAt,
            prioritizedFiles: [],
            manualRun: false,
            cancellationToken);

        var status = await statusService.GetAsync(cancellationToken);
        if (result == BackupExecutionResult.NoChanges)
        {
            status.LastRun = DateTimeOffset.UtcNow;
            status.LastBackupStatus = "NoChanges";
            status.LastOperationStatus = "NoChanges";
            status.LastOperationMessage = "No new or changed files or folders were found.";
            status.LastOperationFinishedAt = DateTimeOffset.UtcNow;
            await statusService.SaveAsync(status, cancellationToken);
            return;
        }

        status = await statusService.GetAsync(cancellationToken);
        status.LastFullBackup = DateTimeOffset.UtcNow;
        status.LastRun = DateTimeOffset.UtcNow;
        if (result == BackupExecutionResult.Success)
        {
            status.LastOperationStatus = "Success";
            status.LastOperationMessage = "Backup completed successfully.";
            status.LastOperationFinishedAt = DateTimeOffset.UtcNow;
            status.LastError = string.Empty;
        }

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
        var maxUploadBytes = Math.Max(1, config.MaxUploadSizeMB) * 1024L * 1024L;

        foreach (var package in queue.ToList())
        {
            if (!File.Exists(package.FilePath))
            {
                queue.Remove(package);
                changed = true;
                continue;
            }

            try
            {
                var fileInfo = new FileInfo(package.FilePath);
                if (fileInfo.Length == 0 || fileInfo.Length > maxUploadBytes)
                {
                    logger.LogWarning(
                        "Discarding queued package {PackagePath} because its size is {PackageSizeBytes} bytes and exceeds the allowed upload size.",
                        package.FilePath,
                        fileInfo.Length);
                    TryDeletePackage(package.FilePath);
                    queue.Remove(package);
                    changed = true;
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not inspect queued package {PackagePath}.", package.FilePath);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
        if (string.IsNullOrEmpty(_activeRequestId))
        {
            status.LastOperationStatus = "Success";
            status.LastOperationMessage = "Backup completed successfully.";
            status.LastOperationFinishedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            status.LastOperationStatus = "Checking";
            status.LastOperationMessage = "Package uploaded. Checking remaining selected files...";
            status.LastOperationFinishedAt = null;
            status.BackupProgressPercent = null;
        }
        await statusService.SaveAsync(status, cancellationToken);
        await backupStateService.MarkConfirmedAsync(package.IncludedFiles, cancellationToken);
        return true;
    }

    private async Task<BackupExecutionResult> RunBackupPackagesAsync(
        ClientAgentConfig config,
        DateTimeOffset startedAt,
        IReadOnlyCollection<string> prioritizedFiles,
        bool manualRun,
        CancellationToken cancellationToken)
    {
        var uploadedPackages = 0;
        if (config.BackupFolders.Count == 0 && config.CustomFolders.Count == 0)
            return BackupExecutionResult.NoChanges;
        var sources = folderMappingService.GetBackupFolders(config);
        foreach (var source in sources)
            logger.LogInformation("Backup source {SourceName}: {SourcePath}", source.Key, source.Value);
        foreach (var missing in config.BackupFolders.Where(name => !sources.ContainsKey(name)))
            logger.LogWarning("Selected user folder {SourceName} is unavailable for Windows user {Username}.", missing, Environment.UserName);
        if (sources.Count == 0)
            throw new InvalidOperationException("None of the selected backup folders are available for the current Windows user. Check the agent account and server policy.");

        await UpdateOperationStatusAsync("Checking", "Checking that previous backups still exist on the server...",
            startedAt, null, cancellationToken);
        var state = await backupStateService.GetSnapshotAsync(cancellationToken);
        Dictionary<string, BackupFileStateEntry> verifiedState;
        Exception? verificationError = null;
        try
        {
            verifiedState = await apiService.VerifyBackupStateAsync(config, identityService.GetCurrent(), state,
                (done, total) => UpdateOperationStatusAsync("Checking", $"Checking server backups: {done:N0} of {total:N0} files...",
                    startedAt, null, cancellationToken), cancellationToken);
            logger.LogInformation("Server backup check: {VerifiedCount} confirmed; {MissingCount} state records require backup again.",
                verifiedState.Count, state.Count - verifiedState.Count);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            (ex is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500) ||
             ex is OperationCanceledException))
        {
            // Keep capturing local changes for the offline queue, but never call an unchecked run complete.
            verificationError = ex;
            verifiedState = new Dictionary<string, BackupFileStateEntry>(state, StringComparer.OrdinalIgnoreCase);
            logger.LogWarning(ex, "Server verification unavailable; capturing local changes for retry only.");
        }

        for (var packageNumber = 1; packageNumber <= MaxPackagesPerBackupRun; packageNumber++)
        {
            if (packageNumber > 1)
            {
                var refreshed = await configService.GetAsync(cancellationToken);
                // Never send an in-flight run to a different server.
                if (!ClientPolicyStore.SameServer(config.ServerUrl, refreshed.ServerUrl))
                    return BackupExecutionResult.Queued;
                config = refreshed;
            }
            var status = await statusService.GetAsync(cancellationToken);
            await UpdateOperationStatusAsync(
                "Packaging",
                GetPackagingMessage(packageNumber, manualRun),
                startedAt,
                finishedAt: null,
                cancellationToken);

            var package = await packageBuilder.CreateSmartBackupAsync(
                config,
                status.LastSuccessfulBackup,
                verifiedState,
                uploadedPackages == 0 ? prioritizedFiles : [],
                cancellationToken,
                async (message, percent) =>
                {
                    var progress = await statusService.GetAsync(cancellationToken);
                    progress.LastOperationRequestId = _activeRequestId;
                    progress.LastOperationStatus = "Packaging";
                    progress.LastOperationMessage = message;
                    progress.BackupProgressPercent = percent;
                    progress.LastOperationFinishedAt = null;
                    await statusService.SaveAsync(progress, cancellationToken);
                });

            if (package is null)
            {
                if (verificationError is not null)
                    throw new InvalidOperationException("Could not verify existing server backups. Check the connection and run backup again.", verificationError);
                return uploadedPackages > 0
                    ? BackupExecutionResult.Success
                    : BackupExecutionResult.NoChanges;
            }

            await EnqueueAndUploadAsync(config, package, retryImmediately: false, cancellationToken);
            await UpdateOperationStatusAsync(
                "Uploading",
                GetUploadingMessage(packageNumber, manualRun),
                startedAt,
                finishedAt: null,
                cancellationToken);
            await TryUploadSinglePackageAsync(config, package, cancellationToken);

            status = await statusService.GetAsync(cancellationToken);
            if (!string.Equals(status.LastBackupStatus, "Success", StringComparison.OrdinalIgnoreCase))
            {
                return BackupExecutionResult.Queued;
            }

            uploadedPackages++;
            // Reuse the check for this run; only successful uploads add new confirmations.
            foreach (var file in package.IncludedFiles) verifiedState[file.SourcePath] = file;
        }

        logger.LogWarning(
            "Stopped backup run after reaching the safety limit of {MaxPackagesPerBackupRun} packages.",
            MaxPackagesPerBackupRun);
        return BackupExecutionResult.Queued;
    }

    private static string GetPackagingMessage(int packageNumber, bool manualRun)
    {
        var prefix = manualRun ? "Packaging backup package" : "Preparing backup package";
        return $"{prefix} {packageNumber}...";
    }

    private static string GetUploadingMessage(int packageNumber, bool manualRun)
    {
        var prefix = manualRun ? "Uploading backup package" : "Uploading scheduled backup package";
        return $"{prefix} {packageNumber} to the server...";
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

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(folderPath)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                logger.LogWarning(ex, "Could not watch selected source {SourcePath}; other sources remain active.", folderPath);
            }
        }
    }

    private string WatcherRootsKey(ClientAgentConfig config) => string.Join("\n",
        folderMappingService.GetBackupFolders(config).OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}={item.Value}"));

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.FullPath))
        {
            _changedFiles[args.FullPath] = DateTimeOffset.UtcNow;
        }
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
        status.LastOperationRequestId = _activeRequestId;
        status.BackupProgressPercent = null;
        if (!string.IsNullOrEmpty(_activeRequestId) && finishedAt.HasValue)
        {
            status.CompletedBackupRequestId = _activeRequestId;
            status.CompletedBackupRequestStatus = operationStatus;
            status.CompletedBackupRequestMessage = operationMessage;
        }
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
        else if (string.Equals(operationStatus, "Success", StringComparison.OrdinalIgnoreCase))
        {
            status.LastBackupStatus = "Success";
            status.LastSuccessfulBackup = DateTimeOffset.UtcNow;
            status.LastError = string.Empty;
        }

        await statusService.SaveAsync(status, cancellationToken);
    }

    private async Task ProcessRequestedBackupAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        if (!File.Exists(LinkSafeDefaults.ClientRunBackupSignalPath))
        {
            if (remoteInbox is not null && config.IsAgentEnabled)
                await remoteInbox.ExecuteAsync(config.ServerUrl, async (id, token) =>
                {
                    _activeRequestId = id;
                    try
                    {
                        await RunImmediateFullBackupAsync(token);
                        var status = await statusService.GetAsync(token);
                        return status.CompletedBackupRequestId == id
                            ? (status.CompletedBackupRequestStatus, status.CompletedBackupRequestMessage)
                            : ("Failed", "The agent did not confirm completion of this server request.");
                    }
                    finally { _activeRequestId = string.Empty; }
                }, cancellationToken);
            return;
        }

        var request = (await File.ReadAllTextAsync(LinkSafeDefaults.ClientRunBackupSignalPath, cancellationToken)).Trim();
        TryDeletePackage(LinkSafeDefaults.ClientRunBackupSignalPath);
        _activeRequestId = Guid.TryParse(request, out var id) ? id.ToString("D") : Guid.NewGuid().ToString("D");
        try { await RunImmediateFullBackupAsync(cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Requested backup failed; the agent will remain available for retries.");
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) _interruptedRequestId = _activeRequestId;
            _activeRequestId = string.Empty;
        }
    }

    private async Task RunStoppableBackupAsync(Func<CancellationToken, Task> work, CancellationToken lifetime)
    {
        var stopId = await new BackupStopControl(LinkSafeDefaults.ClientStopBackupSignalPath).RunAsync(work, lifetime);
        if (string.IsNullOrEmpty(stopId)) return;
        var status = await statusService.GetAsync(lifetime);
        if (status.LastStopRequestId == stopId) return;
        var pending = string.Empty;
        try { pending = (await File.ReadAllTextAsync(LinkSafeDefaults.ClientRunBackupSignalPath, lifetime)).Trim(); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        File.Delete(LinkSafeDefaults.ClientRunBackupSignalPath);
        if (string.IsNullOrEmpty(pending)) pending = _interruptedRequestId;
        BackupStopControl.MarkStopped(status, stopId, pending);
        await statusService.SaveAsync(status, lifetime);
        _interruptedRequestId = string.Empty;
        logger.LogInformation("Backup stopped by user. Automatic backups and retries are paused until Back up now; queued packages are retained.");
    }

    private enum BackupExecutionResult
    {
        NoChanges,
        Success,
        Queued
    }
}
