using Shared.Constants;
using Shared.Models;
using Shared.Storage;

namespace LinkSafeServer.Services;

public sealed class BackupPresenceService(ServerConfigService configService, ClientRegistryService registry)
{
    public async Task<BackupPresenceResponse> CheckAsync(BackupPresenceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DomainUser) || string.IsNullOrWhiteSpace(request.ComputerName) ||
            request.Files is null || request.Files.Count > BackupPresenceRequest.BatchSize)
            throw new ArgumentException("A registered client and at most 500 files are required.");
        var client = await registry.GetAsync(request.DomainUser, request.ComputerName, cancellationToken);
        if (client is null || client.IsDisabled) throw new UnauthorizedAccessException("Client is not registered or is disabled.");
        var config = await configService.GetAsync(cancellationToken);
        // Resolve exactly the same destination as uploads, not a cached registry destination.
        var root = RestorePathSafety.UnderRoot(config.BackupRoot, $"{client.UserFolderName}/{client.ComputerFolderName}");
        var result = new BackupPresenceResponse { SupportsDirectories = true };
        foreach (var file in request.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) || file.Length < 0)
                throw new ArgumentException("Invalid backup file description.");
            var relative = RestorePathSafety.NormalizeRelative(file.RelativePath);
            var source = relative.Split('/')[0];
            if (!LinkSafeDefaults.KnownFolders.Contains(source, StringComparer.OrdinalIgnoreCase) &&
                !source.Equals("Drives", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Unsupported backup source.");
            var path = RestorePathSafety.UnderRoot(root, relative);
            try
            {
                var attributes = File.GetAttributes(path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory != file.IsDirectory) continue;
                if (isDirectory || new FileInfo(path).Length == file.Length) result.PresentPaths.Add(relative);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }
}
