using System.IO.Compression;
using System.Text.Json;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class BackupPackageBuilder(
    FolderMappingService folderMappingService,
    ClientIdentityService clientIdentityService,
    ILogger<BackupPackageBuilder> logger,
    string? queueDirectory = null)
{
    private static readonly DateTimeOffset MinZipTimestamp = new(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local));
    private static readonly DateTimeOffset MaxZipTimestamp = new(new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local));

    public async Task<QueuePackageRecord?> CreateFullBackupAsync(ClientAgentConfig config, CancellationToken cancellationToken,
        Func<string, int?, Task>? reportProgress = null)
    {
        var folderMap = folderMappingService.GetBackupFolders(config);
        var files = await CollectCandidatesAsync(EnumerateCandidateFiles(config, folderMap, cancellationToken: cancellationToken), reportProgress, cancellationToken);
        var directories = await CollectCandidatesAsync(EnumerateCandidateDirectories(config, folderMap, cancellationToken), reportProgress, cancellationToken);
        logger.LogInformation(
            "Preparing full backup package from {FolderCount} folders with {CandidateCount} candidate files and {DirectoryCount} directories.",
            folderMap.Count,
            files.Count,
            directories.Count);
        return await CreatePackageAsync(config, folderMap, files, directories, "Full", cancellationToken, reportProgress);
    }

    public async Task<QueuePackageRecord?> CreateIncrementalBackupAsync(
        ClientAgentConfig config,
        IReadOnlyCollection<string> changedFiles,
        CancellationToken cancellationToken)
    {
        var folderMap = folderMappingService.GetBackupFolders(config);
        var allowedFiles = changedFiles
            .Where(File.Exists)
            .Where(path => TryGetEntryPath(path, folderMap, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation("Preparing incremental backup package with {CandidateCount} candidate files.", allowedFiles.Count);
        return await CreatePackageAsync(config, folderMap, allowedFiles, directories: [], "Incremental", cancellationToken);
    }

    public async Task<QueuePackageRecord?> CreateSmartBackupAsync(
        ClientAgentConfig config,
        DateTimeOffset? lastSuccessfulBackup,
        IReadOnlyDictionary<string, BackupFileStateEntry> backupState,
        IReadOnlyCollection<string> prioritizedFiles,
        CancellationToken cancellationToken,
        Func<string, int?, Task>? reportProgress = null)
    {
        if (lastSuccessfulBackup is null || backupState.Count == 0)
        {
            return await CreateFullBackupAsync(config, cancellationToken, reportProgress);
        }

        var folderMap = folderMappingService.GetBackupFolders(config);
        var recentFiles = EnumerateCandidateFiles(config, folderMap, file =>
        {
            var snapshot = SafeGetSnapshot(file);
            if (snapshot is not null && TryGetEntryPath(file, folderMap, out var entryPath))
                snapshot.ArchiveEntryPath = entryPath;
            return snapshot is not null && IsNewOrChanged(snapshot, backupState);
        }, cancellationToken);

        var allowedPrioritizedFiles = prioritizedFiles
            .Where(File.Exists)
            .Where(path => TryGetEntryPath(path, folderMap, out _))
            .ToList();

        var files = await CollectCandidatesAsync(allowedPrioritizedFiles.Concat(recentFiles), reportProgress, cancellationToken);
        var directories = await CollectCandidatesAsync(
            EnumerateCandidateDirectories(config, folderMap, cancellationToken).Where(path =>
                TryGetEntryPath(path, folderMap, out var entryPath) &&
                (!backupState.TryGetValue(path, out var previous) || !previous.IsDirectory ||
                 !string.Equals(previous.ArchiveEntryPath, entryPath, StringComparison.OrdinalIgnoreCase))),
            reportProgress, cancellationToken);

        logger.LogInformation(
            "Preparing smart backup package with {CandidateCount} candidate files using {StateCount} confirmed state records.",
            files.Count,
            backupState.Count);

        return await CreatePackageAsync(config, folderMap, files, directories, "Incremental", cancellationToken, reportProgress);
    }

    private static async Task<List<string>> CollectCandidatesAsync(IEnumerable<string> candidates,
        Func<string, int?, Task>? reportProgress, CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextReport = DateTime.UtcNow;
        if (reportProgress is not null) await reportProgress("Scanning selected user folders and additional drives...", null);
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(file);
            if (reportProgress is not null && DateTime.UtcNow >= nextReport)
            {
                await reportProgress($"Scanning: {files.Count:N0} eligible items found...", null);
                nextReport = DateTime.UtcNow.AddSeconds(1);
            }
        }
        return files.ToList();
    }

    private async Task<QueuePackageRecord?> CreatePackageAsync(
        ClientAgentConfig config,
        IReadOnlyDictionary<string, string> folderMap,
        IReadOnlyCollection<string> files,
        IReadOnlyCollection<string> directories,
        string backupType,
        CancellationToken cancellationToken,
        Func<string, int?, Task>? reportProgress = null)
    {
        if (files.Count == 0 && directories.Count == 0)
        {
            return null;
        }

        var packageDirectory = queueDirectory ?? LinkSafeDefaults.ClientQueueDirectory;
        Directory.CreateDirectory(packageDirectory);

        var identity = clientIdentityService.GetCurrent();
        var backupJobId = Guid.NewGuid().ToString("D");
        var zipPath = Path.Combine(packageDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{backupJobId}.zip");
        var startedAt = DateTimeOffset.UtcNow;
        long totalSizeBytes = 0;
        var includedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedFiles = new List<BackupFileStateEntry>();
        var fileCount = 0;
        var directoryCount = 0;
        long directoryHeaderBytes = 0;
        var maxPackageBytes = Math.Max(1, config.MaxUploadSizeMB) * 1024L * 1024L;

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var directoryPath in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryGetEntryPath(directoryPath, folderMap, out var directoryEntryPath))
                    {
                        if (!Directory.Exists(directoryPath) ||
                            (File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0) continue;
                        var normalizedDirectoryEntryPath = directoryEntryPath.TrimEnd('/') + "/";
                        var headerBytes = 128L + 2 * System.Text.Encoding.UTF8.GetByteCount(normalizedDirectoryEntryPath);
                        // Bound directory-only packages; confirmed entries are skipped by the next scan.
                        if (directoryCount >= 2000 || directoryHeaderBytes + headerBytes > maxPackageBytes / 2) break;
                        archive.CreateEntry(normalizedDirectoryEntryPath, CompressionLevel.Fastest);
                        directoryCount++;
                        directoryHeaderBytes += headerBytes;
                        includedFiles.Add(new BackupFileStateEntry
                        {
                            SourcePath = directoryPath, ArchiveEntryPath = directoryEntryPath,
                            IsDirectory = true, ConfirmedAt = DateTimeOffset.UtcNow
                        });
                        includedFolders.Add(normalizedDirectoryEntryPath.Split('/')[0]);
                    }
                }

                var examined = 0;
                var nextProgress = DateTime.UtcNow;
                foreach (var filePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    examined++;
                    if (reportProgress is not null && DateTime.UtcNow >= nextProgress)
                    {
                        await reportProgress($"Packaging file {examined:N0} of {files.Count:N0}: {Path.GetFileName(filePath)}", (examined - 1) * 100 / Math.Max(1, files.Count));
                        nextProgress = DateTime.UtcNow.AddSeconds(1);
                    }

                    if (!TryGetEntryPath(filePath, folderMap, out var entryPath))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    if (!ShouldIncludeFile(config, info))
                    {
                        continue;
                    }

                    if (info.Length > maxPackageBytes)
                    {
                        logger.LogWarning(
                            "Skipping file {FilePath} because its size of {FileSizeBytes} bytes exceeds the configured upload size limit.",
                            filePath,
                            info.Length);
                        continue;
                    }

                    if (fileCount > 0 && totalSizeBytes + info.Length > maxPackageBytes)
                    {
                        logger.LogInformation(
                            "Stopping package growth at {CurrentSizeBytes} bytes so the backup remains within the configured upload size.",
                            totalSizeBytes);
                        break;
                    }

                    try
                    {
                        await using var source = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampZipTimestamp(info.LastWriteTime);
                        await using var entryStream = entry.Open();
                        await source.CopyToAsync(entryStream, cancellationToken);
                        totalSizeBytes += info.Length;
                        fileCount++;
                        var snapshot = CreateSnapshot(info);
                        snapshot.ArchiveEntryPath = entryPath;
                        includedFiles.Add(snapshot);
                        includedFolders.Add(entryPath.Split('/')[0]);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        logger.LogWarning(ex, "Skipping inaccessible file {FilePath}.", filePath);
                    }
                    catch (IOException ex)
                    {
                        logger.LogWarning(ex, "Skipping locked or unreadable file {FilePath}.", filePath);
                    }
                }

                if (fileCount == 0 && directoryCount == 0)
                {
                    logger.LogWarning("Backup package {PackagePath} contained no eligible files and will be discarded.", zipPath);
                    archive.Dispose();
                    TryDeletePackage(zipPath);
                    return null;
                }

                var metadata = new BackupUploadMetadata
                {
                    DomainUser = identity.DomainUser,
                    Username = identity.Username,
                    Domain = identity.Domain,
                    ComputerName = identity.ComputerName,
                    AgentVersion = "1.0.16",
                    BackupJobId = backupJobId,
                    BackupType = backupType,
                    BackupStartedAt = startedAt,
                    BackupFinishedAt = DateTimeOffset.UtcNow,
                    IncludedFolders = includedFolders.ToList(),
                    FileCount = fileCount,
                    TotalSizeBytes = totalSizeBytes
                };

                var metadataEntry = archive.CreateEntry("metadata.json", CompressionLevel.Fastest);
                await using var metadataStream = metadataEntry.Open();
                await JsonSerializer.SerializeAsync(metadataStream, metadata, cancellationToken: cancellationToken);
            }
        }
        catch
        {
            TryDeletePackage(zipPath);
            throw;
        }

        var metadataJson = JsonSerializer.Serialize(new BackupUploadMetadata
        {
            DomainUser = identity.DomainUser,
            Username = identity.Username,
            Domain = identity.Domain,
            ComputerName = identity.ComputerName,
            AgentVersion = "1.0.16",
            BackupJobId = backupJobId,
            BackupType = backupType,
            BackupStartedAt = startedAt,
            BackupFinishedAt = DateTimeOffset.UtcNow,
            IncludedFolders = includedFolders.ToList(),
            FileCount = fileCount,
            TotalSizeBytes = totalSizeBytes
        });

        logger.LogInformation(
            "Created {BackupType} backup package {PackagePath} with {FileCount} files, {DirectoryCount} folders and {TotalSizeBytes} bytes.",
            backupType,
            zipPath,
            fileCount,
            directoryCount,
            totalSizeBytes);

        return new QueuePackageRecord
        {
            PackageId = backupJobId,
            FilePath = zipPath,
            MetadataJson = metadataJson,
            CreatedAt = DateTimeOffset.UtcNow,
            IncludedFiles = includedFiles
        };
    }

    private IEnumerable<string> EnumerateCandidateFiles(
        ClientAgentConfig config,
        IReadOnlyDictionary<string, string> folderMap,
        Func<string, bool>? additionalFilter = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var (_, folderPath) in folderMap)
        {
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            foreach (var filePath in EnumerateFilesSafely(folderPath, config, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(filePath);
                if (ShouldIncludeFile(config, info) && (additionalFilter is null || additionalFilter(filePath)))
                {
                    yield return filePath;
                }
            }
        }
    }

    private IEnumerable<string> EnumerateCandidateDirectories(
        ClientAgentConfig config,
        IReadOnlyDictionary<string, string> folderMap,
        CancellationToken cancellationToken)
    {
        foreach (var (_, folderPath) in folderMap)
        {
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            foreach (var directoryPath in EnumerateDirectoriesSafely(folderPath, config, cancellationToken))
            {
                if (!IsExcludedByDirectoryRule(config, directoryPath))
                {
                    yield return directoryPath;
                }
            }
        }
    }

    private bool ShouldIncludeFile(ClientAgentConfig config, FileInfo info)
    {
        if (info.Name.StartsWith(".linksafe-restore-", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var maxSingleFileBytes = Math.Min(
                Math.Max(1, config.MaxFileSizeMB) * 1024L * 1024L,
                Math.Max(1, config.MaxUploadSizeMB) * 1024L * 1024L);
            if (info.Length > maxSingleFileBytes)
            {
                return false;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Skipping inaccessible file metadata for {FilePath}.", info.FullName);
            return false;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Skipping unreadable file metadata for {FilePath}.", info.FullName);
            return false;
        }

        if (IsExcludedByDirectoryRule(config, info.FullName))
        {
            return false;
        }

        return !config.ExcludeFiles.Any(pattern => MatchesPattern(info.Name, pattern));
    }

    private static bool IsExcludedByDirectoryRule(ClientAgentConfig config, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var protectedPaths = config.ProtectedSourcePaths.Concat(new[]
        {
            LinkSafeDefaults.ClientDataRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LinkSafeClient"),
            Path.Combine(programData, "LinkSafeAgent"),
            Path.Combine(programData, "LinkSafeServer"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            AppContext.BaseDirectory
        });
        if (protectedPaths.Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root => BackupSourcePaths.IsWithin(fullPath, root)))
            return true;
        var leaf = Path.GetFileName(fullPath);
        if (new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" }.Contains(leaf, StringComparer.OrdinalIgnoreCase))
            return true;
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => segment.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
            return true;
        foreach (var excluded in config.ExcludeDirectories.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var excludedSegments = excluded.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (ContainsDirectorySequence(segments, excludedSegments))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDirectorySequence(string[] pathSegments, string[] excludedSegments)
    {
        if (excludedSegments.Length == 0 || pathSegments.Length < excludedSegments.Length)
        {
            return false;
        }

        for (var start = 0; start <= pathSegments.Length - excludedSegments.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < excludedSegments.Length; offset++)
            {
                if (!pathSegments[start + offset].Equals(excludedSegments[offset], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool TryGetEntryPath(
        string filePath,
        IReadOnlyDictionary<string, string> folderMap,
        out string entryPath)
    {
        foreach (var (folderName, folderRoot) in folderMap)
        {
            var normalizedRoot = Path.GetFullPath(folderRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedFile = Path.GetFullPath(filePath);

            if (normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(folderRoot, filePath).Replace('\\', '/');
                entryPath = $"{folderName}/{relativePath}";
                return true;
            }
        }

        entryPath = string.Empty;
        return false;
    }

    private IEnumerable<string> EnumerateFilesSafely(string rootPath, ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentPath = pending.Pop();
            if (IsExcludedByDirectoryRule(config, currentPath)) continue;

            IEnumerable<string> files = [];
            try
            {
                files = Directory.GetFiles(currentPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Skipping inaccessible directory {DirectoryPath}.", currentPath);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Skipping unreadable directory {DirectoryPath}.", currentPath);
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> directories = [];
            try
            {
                directories = Directory.GetDirectories(currentPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Skipping inaccessible subdirectories under {DirectoryPath}.", currentPath);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Skipping unreadable subdirectories under {DirectoryPath}.", currentPath);
            }

            foreach (var directory in directories)
            {
                DirectoryInfo? directoryInfo = null;
                try
                {
                    directoryInfo = new DirectoryInfo(directory);
                    if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        logger.LogInformation("Skipping reparse point directory {DirectoryPath}.", directory);
                        continue;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogWarning(ex, "Skipping inaccessible directory metadata for {DirectoryPath}.", directory);
                    continue;
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Skipping unreadable directory metadata for {DirectoryPath}.", directory);
                    continue;
                }

                pending.Push(directoryInfo.FullName);
            }
        }
    }

    private IEnumerable<string> EnumerateDirectoriesSafely(string rootPath, ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentPath = pending.Pop();
            if (IsExcludedByDirectoryRule(config, currentPath)) continue;
            IEnumerable<string> directories = [];
            try
            {
                directories = Directory.GetDirectories(currentPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Skipping inaccessible subdirectories under {DirectoryPath}.", currentPath);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Skipping unreadable subdirectories under {DirectoryPath}.", currentPath);
            }

            foreach (var directory in directories)
            {
                DirectoryInfo directoryInfo;
                try
                {
                    directoryInfo = new DirectoryInfo(directory);
                    if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        logger.LogInformation("Skipping reparse point directory {DirectoryPath}.", directory);
                        continue;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogWarning(ex, "Skipping inaccessible directory metadata for {DirectoryPath}.", directory);
                    continue;
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Skipping unreadable directory metadata for {DirectoryPath}.", directory);
                    continue;
                }

                yield return directoryInfo.FullName;
                pending.Push(directoryInfo.FullName);
            }
        }
    }

    private void TryDeletePackage(string zipPath)
    {
        try
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete discarded package {PackagePath}.", zipPath);
        }
    }

    private BackupFileStateEntry? SafeGetSnapshot(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? CreateSnapshot(info) : null;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Skipping inaccessible state lookup for {FilePath}.", filePath);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Skipping unreadable state lookup for {FilePath}.", filePath);
            return null;
        }
    }

    private static BackupFileStateEntry CreateSnapshot(FileInfo info) =>
        new()
        {
            SourcePath = info.FullName,
            Length = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            ConfirmedAt = DateTimeOffset.UtcNow
        };

    private static bool IsNewOrChanged(
        BackupFileStateEntry current,
        IReadOnlyDictionary<string, BackupFileStateEntry> backupState)
    {
        if (!backupState.TryGetValue(current.SourcePath, out var confirmed))
        {
            return true;
        }

        return confirmed.IsDirectory != current.IsDirectory || confirmed.Length != current.Length ||
            confirmed.LastWriteTimeUtc != current.LastWriteTimeUtc ||
            !string.Equals(confirmed.ArchiveEntryPath, current.ArchiveEntryPath, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset ClampZipTimestamp(DateTime value)
    {
        var timestamp = new DateTimeOffset(value);
        if (timestamp < MinZipTimestamp)
        {
            return MinZipTimestamp;
        }

        if (timestamp > MaxZipTimestamp)
        {
            return MaxZipTimestamp;
        }

        return timestamp;
    }
}
