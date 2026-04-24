using Shared.Constants;

namespace Shared.Models;

public sealed class ServerConfig
{
    public string BackupRoot { get; set; } = @"D:\LinkSafeBackups";
    public string IncomingRoot { get; set; } = @"D:\LinkSafeBackups\_Incoming";
    public string QuarantineRoot { get; set; } = @"D:\LinkSafeBackups\_Quarantine";
    public string VersionsRoot { get; set; } = @"D:\LinkSafeBackups\_Versions";
    public string LogsRoot { get; set; } = @"D:\LinkSafeBackups\_Logs";
    public string ServerUrl { get; set; } = LinkSafeDefaults.DefaultServerUrl;
    public string AdminUsername { get; set; } = "admin";
    public string AdminPasswordHash { get; set; } = string.Empty;
    public string AdminGroup { get; set; } = "Administrators";
    public string BackupOperatorsGroup { get; set; } = string.Empty;
    public List<string> BackupFolders { get; set; } = [.. LinkSafeDefaults.DefaultBackupFolders];
    public List<string> CustomFolders { get; set; } = [];
    public List<string> ExcludeDirectories { get; set; } = [.. LinkSafeDefaults.DefaultExcludeDirectories];
    public List<string> ExcludeFiles { get; set; } = [.. LinkSafeDefaults.DefaultExcludeFiles];
    public int BackupIntervalMinutes { get; set; } = 30;
    public int DelayBeforeCopySeconds { get; set; } = 60;
    public int StableFileCheckSeconds { get; set; } = 10;
    public int MaxFileSizeMB { get; set; } = 500;
    public int MaxUploadSizeMB { get; set; } = 1000;
    public bool EnableVersioning { get; set; } = true;
    public bool EnableAdminShare { get; set; }
    public string ServiceAccount { get; set; } = "LocalSystem";

    public bool IsInitialized => !string.IsNullOrWhiteSpace(AdminUsername) && !string.IsNullOrWhiteSpace(AdminPasswordHash);
}
