using System.IO.Compression;
using LinkSafeServer.Pages.Browse;
using Microsoft.AspNetCore.Http;
using Shared.Models;

namespace LinkSafeServer.Services;

public sealed class BackupStorageService(
    ServerConfigService serverConfigService,
    ClientRegistryService clientRegistryService,
    BackupJobService backupJobService,
    StorageSecurityService storageSecurityService,
    PathSanitizer pathSanitizer,
    ILogger<BackupStorageService> logger)
{
    public async Task<BackupUploadResponse> ProcessUploadAsync(
        IFormFile file,
        BackupUploadMetadata metadata,
        CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(metadata.DomainUser) ||
            string.IsNullOrWhiteSpace(metadata.Username) ||
            string.IsNullOrWhiteSpace(metadata.ComputerName))
        {
            return new BackupUploadResponse { Success = false, Message = "Username, domain user, and computer name are required." };
        }

        if (file.Length == 0)
        {
            return new BackupUploadResponse { Success = false, Message = "Uploaded ZIP package is empty." };
        }

        var maxUploadBytes = config.MaxUploadSizeMB * 1024L * 1024L;
        if (file.Length > maxUploadBytes)
        {
            return new BackupUploadResponse { Success = false, Message = "Upload exceeds the configured maximum size." };
        }

        var clientRecord = await clientRegistryService.GetAsync(metadata.DomainUser, metadata.ComputerName, cancellationToken);
        if (clientRecord is null || clientRecord.IsDisabled)
        {
            return new BackupUploadResponse { Success = false, Message = "Client is not registered or is disabled." };
        }

        await storageSecurityService.EnsureStorageStructureAsync(config, cancellationToken);

        var incomingName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.zip";
        var incomingPath = Path.Combine(config.IncomingRoot, incomingName);
        Directory.CreateDirectory(config.IncomingRoot);

        await using (var stream = File.Create(incomingPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var clientRoot = Path.Combine(config.BackupRoot, clientRecord.UserFolderName, clientRecord.ComputerFolderName);
        Directory.CreateDirectory(clientRoot);

        var job = new BackupJobRecord
        {
            BackupJobId = metadata.BackupJobId,
            DomainUser = metadata.DomainUser,
            Username = metadata.Username,
            ComputerName = metadata.ComputerName,
            BackupType = metadata.BackupType,
            StartedAt = metadata.BackupStartedAt,
            FinishedAt = metadata.BackupFinishedAt,
            FileCount = metadata.FileCount,
            TotalSizeBytes = metadata.TotalSizeBytes,
            Status = "Processing"
        };

        await backupJobService.AddOrUpdateAsync(job, cancellationToken);

        try
        {
            var filesProcessed = 0;
            var filesVersioned = 0;

            using (var archive = ZipFile.OpenRead(incomingPath))
            {
                ValidateArchiveEntries(archive, clientRoot);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (entry.FullName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destinationPath = Path.GetFullPath(Path.Combine(clientRoot, relativePath));
                    if (string.IsNullOrWhiteSpace(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                    if (IsExistingFileAlreadyCurrent(destinationPath, entry))
                    {
                        filesProcessed++;
                        continue;
                    }

                    if (config.EnableVersioning && File.Exists(destinationPath))
                    {
                        if (await TryVersionExistingFileAsync(clientRoot, destinationPath, relativePath, cancellationToken))
                        {
                            filesVersioned++;
                        }
                    }

                    await using var entryStream = entry.Open();
                    await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    await entryStream.CopyToAsync(destinationStream, cancellationToken);
                    File.SetLastWriteTimeUtc(destinationPath, entry.LastWriteTime.UtcDateTime);
                    filesProcessed++;
                }
            }

            File.Delete(incomingPath);

            job.Status = "Success";
            job.FilesProcessed = filesProcessed;
            job.FilesVersioned = filesVersioned;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await backupJobService.AddOrUpdateAsync(job, cancellationToken);

            await clientRegistryService.UpdateBackupStatusAsync(
                metadata.DomainUser,
                metadata.ComputerName,
                job.FinishedAt,
                "Success",
                string.Empty,
                cancellationToken);

            return new BackupUploadResponse
            {
                Success = true,
                Message = "Backup uploaded and extracted successfully.",
                FilesProcessed = filesProcessed,
                FilesVersioned = filesVersioned
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup upload failed for {DomainUser} on {ComputerName}.", metadata.DomainUser, metadata.ComputerName);
            Directory.CreateDirectory(config.QuarantineRoot);
            var quarantinePath = Path.Combine(config.QuarantineRoot, incomingName);
            if (File.Exists(incomingPath))
            {
                File.Move(incomingPath, quarantinePath, true);
            }

            job.Status = "Failed";
            job.Error = ex.Message;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await backupJobService.AddOrUpdateAsync(job, cancellationToken);
            await clientRegistryService.UpdateBackupStatusAsync(
                metadata.DomainUser,
                metadata.ComputerName,
                job.FinishedAt,
                "Failed",
                ex.Message,
                cancellationToken);

            return new BackupUploadResponse
            {
                Success = false,
                Message = $"Backup upload failed: {ex.Message}",
                FilesProcessed = job.FilesProcessed,
                FilesVersioned = job.FilesVersioned
            };
        }
    }

    public async Task<IReadOnlyList<BackupBrowseItem>> BrowseAsync(
        string username,
        string computerName,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        var safeUser = pathSanitizer.SanitizeFolderName(username);
        var safeComputer = pathSanitizer.SanitizeFolderName(computerName);

        var root = Path.Combine(config.BackupRoot, safeUser, safeComputer);
        var requestedPath = string.IsNullOrWhiteSpace(relativePath)
            ? root
            : Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!requestedPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(requestedPath))
        {
            return [];
        }

        var directories = Directory.GetDirectories(requestedPath)
            .Select(directory => new DirectoryInfo(directory))
            .Select(info => new BackupBrowseItem
            {
                Name = info.Name,
                RelativePath = Path.GetRelativePath(root, info.FullName),
                IsDirectory = true,
                LastModified = info.LastWriteTimeUtc
            });

        var files = Directory.GetFiles(requestedPath)
            .Select(filePath => new FileInfo(filePath))
            .Select(info => new BackupBrowseItem
            {
                Name = info.Name,
                RelativePath = Path.GetRelativePath(root, info.FullName),
                IsDirectory = false,
                SizeBytes = info.Length,
                LastModified = info.LastWriteTimeUtc
            });

        return directories.Concat(files).OrderBy(item => item.Name).ToList();
    }

    public async Task<DownloadFileResult?> GetFileForDownloadAsync(
        string username,
        string computerName,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var config = await serverConfigService.GetAsync(cancellationToken);
        var root = Path.Combine(config.BackupRoot, pathSanitizer.SanitizeFolderName(username), pathSanitizer.SanitizeFolderName(computerName));
        var filePath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!filePath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            return null;
        }

        return new DownloadFileResult(filePath, Path.GetFileName(filePath));
    }

    private static void ValidateArchiveEntries(ZipArchive archive, string clientRoot)
    {
        var normalizedRoot = Path.GetFullPath(clientRoot);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                throw new InvalidOperationException("ZIP contains an empty entry name.");
            }

            if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':'))
            {
                throw new InvalidOperationException($"ZIP entry '{entry.FullName}' contains a rooted path.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(normalizedRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"ZIP entry '{entry.FullName}' attempts to escape the backup folder.");
            }
        }
    }

    private async Task<bool> TryVersionExistingFileAsync(
        string clientRoot,
        string destinationPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var relativeDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(destinationPath);
            var extension = Path.GetExtension(destinationPath);
            var versionRoot = Path.Combine(clientRoot, "_Versions", DateTime.UtcNow.ToString("yyyy-MM-dd"), relativeDirectory);
            Directory.CreateDirectory(versionRoot);

            var versionedName = $"{baseName}_{DateTime.UtcNow:HHmmssfff}_{Guid.NewGuid():N}{extension}";
            var versionedPath = Path.Combine(versionRoot, versionedName);

            await using var source = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var destination = new FileStream(versionedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await source.CopyToAsync(destination, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not version locked or inaccessible destination file {DestinationPath}. The current backup upload will continue.", destinationPath);
            return false;
        }
    }

    private static bool IsExistingFileAlreadyCurrent(string destinationPath, ZipArchiveEntry entry)
    {
        if (!File.Exists(destinationPath))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(destinationPath);
            if (info.Length != entry.Length)
            {
                return false;
            }

            var delta = (info.LastWriteTimeUtc - entry.LastWriteTime.UtcDateTime).Duration();
            return delta <= TimeSpan.FromSeconds(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
