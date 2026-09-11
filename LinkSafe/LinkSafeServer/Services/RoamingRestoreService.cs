using System.IO.Compression;
using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Services;

public sealed class RoamingRestoreService(
    ClientRegistryService clientRegistryService,
    PathSanitizer pathSanitizer)
{
    private static readonly DateTimeOffset MinZipTimestamp = new(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local));
    private static readonly DateTimeOffset MaxZipTimestamp = new(new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local));

    public async Task<RoamingRestoreOptionsResponse> GetOptionsAsync(
        string domainUser,
        string currentComputerName,
        CancellationToken cancellationToken = default,
        bool includeCurrentComputer = false)
    {
        var normalizedComputerName = currentComputerName.Trim();
        var clients = await clientRegistryService.GetAllAsync(cancellationToken);

        var candidates = clients
            .Where(client => client.DomainUser.Equals(domainUser, StringComparison.OrdinalIgnoreCase))
            .Where(client => !client.IsDisabled)
            .Where(client => includeCurrentComputer || !client.ComputerName.Equals(normalizedComputerName, StringComparison.OrdinalIgnoreCase))
            .Select(client => new RoamingRestoreCandidate
            {
                SourceComputerName = client.ComputerName,
                SourceDomainUser = client.DomainUser,
                BackupPath = client.BackupDestinationPath,
                LastBackupTime = client.LastBackupTime,
                LastSeenAt = client.LastSeenAt,
                AvailableFolders = GetAvailableFolders(client.BackupDestinationPath, includeDrives: true)
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
                    AddFileToArchive(archive, sourcePath, folderName);
                }
            }
        }

        var sanitizedUser = pathSanitizer.SanitizeFolderName(client.Username);
        var sanitizedComputer = pathSanitizer.SanitizeFolderName(client.ComputerName);
        var downloadName = $"{sanitizedUser}-{sanitizedComputer}-restore.zip";

        return new RestoreArchiveResult(tempZipPath, downloadName);
    }

    private static List<string> GetAvailableFolders(string backupDestinationPath, bool includeDrives = false)
    {
        if (!Directory.Exists(backupDestinationPath))
        {
            return [];
        }

        return LinkSafeDefaults.KnownFolders.Concat(includeDrives ? new[] { "Drives" } : [])
            .Where(folder =>
            {
                var path = Path.Combine(backupDestinationPath, folder);
                return Directory.Exists(path) || File.Exists(path);
            })
            .ToList();
    }

    public async Task<RestoreManifest?> GetManifestAsync(string domainUser, string sourceComputerName, CancellationToken cancellationToken)
    {
        var client = await clientRegistryService.GetAsync(domainUser, sourceComputerName, cancellationToken);
        if (client is null || client.IsDisabled || !Directory.Exists(client.BackupDestinationPath)) return null;
        RestorePathSafety.AssertNoLinks(client.BackupDestinationPath);
        var manifest = new RestoreManifest();
        var pending = new Stack<string>(GetAvailableFolders(client.BackupDestinationPath, true)
            .Select(name => Path.Combine(client.BackupDestinationPath, name)));
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RestorePathSafety.AssertNoLinks(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(client.BackupDestinationPath, path).Replace('\\', '/');
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { manifest.Warnings.Add("Skipped linked path: " + relative); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                    var info = new FileInfo(path);
                    manifest.Files.Add(new RestoreFileEntry { RelativePath = RestorePathSafety.NormalizeRelative(relative), Length = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc });
                    if (manifest.Files.Count > 200000) throw new InvalidOperationException("This restore source exceeds the 200,000-file manifest limit. Split the source before restoring.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                manifest.Warnings.Add("Could not fully inspect " + Path.GetRelativePath(client.BackupDestinationPath, directory));
            }
        }
        return manifest;
    }

    public async Task<FileStream?> OpenRestoreFileAsync(string domainUser, string sourceComputerName, string relativePath,
        long length, long lastWriteTicks, CancellationToken cancellationToken)
    {
        var relative = RestorePathSafety.NormalizeRelative(relativePath);
        var folder = relative.Split('/')[0];
        if (!LinkSafeDefaults.KnownFolders.Contains(folder, StringComparer.OrdinalIgnoreCase) && folder != "Drives")
            throw new InvalidDataException("Unsupported restore source.");
        var client = await clientRegistryService.GetAsync(domainUser, sourceComputerName, cancellationToken);
        if (client is null || client.IsDisabled) return null;
        var path = RestorePathSafety.UnderRoot(client.BackupDestinationPath, relative);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        try
        {
            if (stream.Length != length || File.GetLastWriteTimeUtc(path).Ticks != lastWriteTicks)
                throw new InvalidOperationException("Server file changed since comparison. Check again before restoring.");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string archiveRoot)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            AddFileToArchive(archive, filePath, $"{archiveRoot}/{relativePath}");
        }
    }

    private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        var fileInfo = new FileInfo(filePath);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        entry.LastWriteTime = ClampZipTimestamp(fileInfo.LastWriteTime);

        using var source = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = entry.Open();
        source.CopyTo(target);
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

public sealed record RestoreArchiveResult(string ArchivePath, string DownloadName);
