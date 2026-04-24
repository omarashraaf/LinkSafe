using Shared.Models;

namespace LinkSafeClientAgent.Services;

public sealed class FolderMappingService
{
    public IReadOnlyDictionary<string, string> GetBackupFolders(ClientAgentConfig config)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in config.BackupFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = ResolveKnownFolder(folder);
            if (!string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved))
            {
                folders[folder] = resolved;
            }
        }

        foreach (var customFolder in config.CustomFolders.Where(Directory.Exists))
        {
            folders[Path.GetFileName(customFolder)] = customFolder;
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
        "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Favorites" => Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
        "AppDataRoaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChromeProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"),
        "EdgeProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data"),
        "FirefoxProfile" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox"),
        _ => string.Empty
    };
}
