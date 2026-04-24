using System.IO.Compression;
using Shared.Constants;
using Shared.Models;

namespace LinkSafeServer.Services;

public sealed class RoamingRestoreService(
    ClientRegistryService clientRegistryService,
    PathSanitizer pathSanitizer)
{
    public async Task<RoamingRestoreOptionsResponse> GetOptionsAsync(
        string domainUser,
        string currentComputerName,
        CancellationToken cancellationToken = default)
    {
        var normalizedComputerName = currentComputerName.Trim();
        var clients = await clientRegistryService.GetAllAsync(cancellationToken);

        var candidates = clients
            .Where(client => client.DomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase))
            .Where(client => !client.ComputerName.Equals(normalizedComputerName, StringComparison.OrdinalIgnoreCase))
            .Select(client => new RoamingRestoreCandidate
            {
                SourceComputerName = client.ComputerName,
                SourceDomainUser = client.DomainUser,
                BackupPath = client.BackupDestinationPath,
                LastBackupTime = client.LastBackupTime,
                LastSeenAt = client.LastSeenAt,
                AvailableFolders = GetAvailableFolders(client.BackupDestinationPath)
            })
            .Where(candidate => candidate.AvailableFolders.Count > 0)
            .OrderByDescending(candidate => candidate.LastBackupTime ?? candidate.LastSeenAt)
            .ToList();

        return new RoamingRestoreOptionsResponse
        {
            DomainUser = domainUser,
            CurrentComputerName = currentComputerName,
            Candidates = candidates
        };
    }

    public async Task<RestoreArchiveResult?> CreateRestoreArchiveAsync(
        string domainUser,
        string sourceComputerName,
        CancellationToken cancellationToken = default)
    {
        var client = await clientRegistryService.GetAsync(domainUser, sourceComputerName, cancellationToken);
        if (client is null || !Directory.Exists(client.BackupDestinationPath))
        {
            return null;
        }

        var availableFolders = GetAvailableFolders(client.BackupDestinationPath);
        if (availableFolders.Count == 0)
        {
            return null;
        }

        var restoreTempDirectory = Path.Combine(Path.GetTempPath(), "LinkSafeServer", "RestorePackages");
        Directory.CreateDirectory(restoreTempDirectory);

        var tempZipPath = Path.Combine(
            restoreTempDirectory,
            $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.zip");

        using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
        {
            foreach (var folderName in availableFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePath = Path.Combine(client.BackupDestinationPath, folderName);
                if (Directory.Exists(sourcePath))
                {
                    AddDirectoryToArchive(archive, sourcePath, folderName);
                    continue;
                }

                if (File.Exists(sourcePath))
                {
                    archive.CreateEntryFromFile(sourcePath, folderName, CompressionLevel.Fastest);
                }
            }
        }

        var sanitizedUser = pathSanitizer.SanitizeFolderName(client.Username);
        var sanitizedComputer = pathSanitizer.SanitizeFolderName(client.ComputerName);
        var downloadName = $"{sanitizedUser}-{sanitizedComputer}-restore.zip";

        return new RestoreArchiveResult(tempZipPath, downloadName);
    }

    private static List<string> GetAvailableFolders(string backupDestinationPath)
    {
        if (!Directory.Exists(backupDestinationPath))
        {
            return [];
        }

        return LinkSafeDefaults.KnownFolders
            .Where(folder =>
            {
                var path = Path.Combine(backupDestinationPath, folder);
                return Directory.Exists(path) || File.Exists(path);
            })
            .ToList();
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string archiveRoot)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            archive.CreateEntryFromFile(filePath, $"{archiveRoot}/{relativePath}", CompressionLevel.Fastest);
        }
    }
}

public sealed record RestoreArchiveResult(string ArchivePath, string DownloadName);
