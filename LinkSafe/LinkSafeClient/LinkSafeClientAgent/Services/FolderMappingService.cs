using Shared.Models;
using Shared.Storage;

namespace LinkSafeClientAgent.Services;

public sealed class FolderMappingService(Func<string, string>? knownFolderResolver = null)
{
    public static List<string> GetAvailableDrives()
    {
        var result = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                    result.Add(drive.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public IReadOnlyDictionary<string, string> GetBackupFolders(ClientAgentConfig config)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in config.BackupFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = (knownFolderResolver ?? ResolveKnownFolder)(folder);
            if (!string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved))
            {
                folders[folder] = resolved;
            }
        }

        var availableDrives = GetAvailableDrives();
        foreach (var customFolder in config.CustomFolders)
        {
            if (BackupSourcePaths.TryNormalize(customFolder, out var path) &&
                availableDrives.Contains(path[..3], StringComparer.OrdinalIgnoreCase) && Directory.Exists(path))
                folders[BackupSourcePaths.ArchiveRoot(path)] = path;
        }

        return folders;
    }

    public bool TryResolveRestoreFolder(string folderName, out string folderPath)
    {
        folderPath = ResolveKnownFolder(folderName);
        return !string.IsNullOrWhiteSpace(folderPath);
    }

    private static string ResolveKnownFolder(string folderName) => folderName switch
    {
        "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify),
        "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
        "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "Downloads"),
        "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify),
        "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify),
        "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic, Environment.SpecialFolderOption.DoNotVerify),
        "Favorites" => Environment.GetFolderPath(Environment.SpecialFolder.Favorites, Environment.SpecialFolderOption.DoNotVerify),
        "AppDataRoaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "ChromeProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Google", "Chrome", "User Data"),
        "EdgeProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Microsoft", "Edge", "User Data"),
        "FirefoxProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Mozilla", "Firefox"),
        _ => string.Empty
    };
}
