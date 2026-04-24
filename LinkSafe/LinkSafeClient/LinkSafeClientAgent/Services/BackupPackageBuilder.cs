using System.IO.Compression;
using System.Text.Json;
using Shared.Constants;
using Shared.Models;

namespace LinkSafeClientAgent.Services;

public sealed class BackupPackageBuilder(
    FolderMappingService folderMappingService,
    ClientIdentityService clientIdentityService,
    ILogger<BackupPackageBuilder> logger)
{
    public async Task<QueuePackageRecord?> CreateFullBackupAsync(ClientAgentConfig config, CancellationToken cancellationToken)
    {
        var folderMap = folderMappingService.GetBackupFolders(config);
        var files = EnumerateCandidateFiles(config, folderMap).ToList();
        var directories = EnumerateCandidateDirectories(config, folderMap).ToList();
        logger.LogInformation(
            "Preparing full backup package from {FolderCount} folders with {CandidateCount} candidate files and {DirectoryCount} directories.",
            folderMap.Count,
            files.Count,
            directories.Count);
        return await CreatePackageAsync(config, folderMap, files, directories, "Full", cancellationToken);
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
        CancellationToken cancellationToken)
    {
        if (lastSuccessfulBackup is null || backupState.Count == 0)
        {
            return await CreateFullBackupAsync(config, cancellationToken);
        }

        var folderMap = folderMappingService.GetBackupFolders(config);
        var recentFiles = EnumerateCandidateFiles(config, folderMap, file =>
        {
            var snapshot = SafeGetSnapshot(file);
            return snapshot is not null && IsNewOrChanged(snapshot, backupState);
        });

        var allowedPrioritizedFiles = prioritizedFiles
            .Where(File.Exists)
            .Where(path => TryGetEntryPath(path, folderMap, out _))
            .ToList();

        var files = allowedPrioritizedFiles
            .Concat(recentFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation(
            "Preparing smart backup package with {CandidateCount} candidate files using {StateCount} confirmed state records.",
            files.Count,
            backupState.Count);

        return await CreatePackageAsync(config, folderMap, files, directories: [], "Incremental", cancellationToken);
    }

    private async Task<QueuePackageRecord?> CreatePackageAsync(
        ClientAgentConfig config,
        IReadOnlyDictionary<string, string> folderMap,
        IReadOnlyCollection<string> files,
        IReadOnlyCollection<string> directories,
        string backupType,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0 && directories.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(LinkSafeDefaults.ClientQueueDirectory);

        var identity = clientIdentityService.GetCurrent();
        var backupJobId = Guid.NewGuid().ToString("D");
        var zipPath = Path.Combine(LinkSafeDefaults.ClientQueueDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{backupJobId}.zip");
        var startedAt = DateTimeOffset.UtcNow;
        long totalSizeBytes = 0;
        var includedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedFiles = new List<BackupFileStateEntry>();
        var fileCount = 0;

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var directoryPath in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryGetEntryPath(directoryPath, folderMap, out var directoryEntryPath))
                    {
                        var normalizedDirectoryEntryPath = directoryEntryPath.TrimEnd('/') + "/";
                        archive.CreateEntry(normalizedDirectoryEntryPath, CompressionLevel.Fastest);
                        includedFolders.Add(normalizedDirectoryEntryPath.Split('/')[0]);
                    }
                }

                foreach (var filePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryGetEntryPath(filePath, folderMap, out var entryPath))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    if (!ShouldIncludeFile(config, info))
                    {
                        continue;
                    }

                    try
                    {
                        await using var source = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                        entry.LastWriteTime = info.LastWriteTime;
                        await using var entryStream = entry.Open();
                        await source.CopyToAsync(entryStream, cancellationToken);
                        totalSizeBytes += info.Length;
                        fileCount++;
                        includedFiles.Add(CreateSnapshot(info));
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

                if (fileCount == 0 && directories.Count == 0)
                {
                    logger.LogWarning("Backup package {PackagePath} contained no eligible files and will be discarded.", zipPath);
                    TryDeletePackage(zipPath);
                    return null;
                }

                var metadata = new BackupUploadMetadata
                {
                    DomainUser = identity.DomainUser,
                    Username = identity.Username,
                    Domain = identity.Domain,
                    ComputerName = identity.ComputerName,
                    AgentVersion = "1.0.0",
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
            AgentVersion = "1.0.0",
            BackupJobId = backupJobId,
            BackupType = backupType,
            BackupStartedAt = startedAt,
            BackupFinishedAt = DateTimeOffset.UtcNow,
            IncludedFolders = includedFolders.ToList(),
            FileCount = fileCount,
            TotalSizeBytes = totalSizeBytes
        });

        logger.LogInformation(
            "Created {BackupType} backup package {PackagePath} with {FileCount} files and {TotalSizeBytes} bytes.",
            backupType,
            zipPath,
            fileCount,
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
        Func<string, bool>? additionalFilter = null)
    {
        foreach (var (_, folderPath) in folderMap)
        {
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            foreach (var filePath in EnumerateFilesSafely(folderPath))
            {
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
        IReadOnlyDictionary<string, string> folderMap)
    {
        foreach (var (_, folderPath) in folderMap)
        {
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            foreach (var directoryPath in EnumerateDirectoriesSafely(folderPath))
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
        try
        {
            if (!info.Exists)
            {
                return false;
            }

            if (info.Length > config.MaxFileSizeMB * 1024L * 1024L)
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
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    private IEnumerable<string> EnumerateFilesSafely(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var currentPath = pending.Pop();

            IEnumerable<string> files = [];
            try
            {
                files = Directory.EnumerateFiles(currentPath);
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
                directories = Directory.EnumerateDirectories(currentPath);
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

    private IEnumerable<string> EnumerateDirectoriesSafely(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var currentPath = pending.Pop();
            IEnumerable<string> directories = [];
            try
            {
                directories = Directory.EnumerateDirectories(currentPath);
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

        return confirmed.Length != current.Length ||
            confirmed.LastWriteTimeUtc != current.LastWriteTimeUtc;
    }
}
