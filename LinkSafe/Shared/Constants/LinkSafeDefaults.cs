namespace Shared.Constants;

public static class LinkSafeDefaults
{
    public const string DefaultServerUrl = "http://BACKUP-SERVER:5055";
    public const string ServerConfigPath = @"C:\ProgramData\LinkSafeServer\server-config.json";
    public const string RegisteredClientsPath = @"C:\ProgramData\LinkSafeServer\registered-clients.json";
    public const string BackupJobsPath = @"C:\ProgramData\LinkSafeServer\backup-jobs.json";
    public const string UserFolderMapPath = @"C:\ProgramData\LinkSafeServer\user-folder-map.json";
    public const string ServerLogsDirectory = @"C:\ProgramData\LinkSafeServer\Logs";
    public const string ServerLogFilePath = @"C:\ProgramData\LinkSafeServer\Logs\backup-server.log";
    public const string ServerDataProtectionKeysDirectory = @"C:\ProgramData\LinkSafeServer\DataProtectionKeys";

    public const string LegacyClientRoot = @"C:\ProgramData\LinkSafeAgent";
    public static string ClientDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkSafeAgent");
    public static string ClientConfigPath => Path.Combine(ClientDataRoot, "client-agent-config.json");
    public static string ClientStatusPath => Path.Combine(ClientDataRoot, "status.json");
    public static string ClientQueuePath => Path.Combine(ClientDataRoot, "queue.json");
    public static string ClientBackupStatePath => Path.Combine(ClientDataRoot, "backup-state.json");
    public static string ClientQueueDirectory => Path.Combine(ClientDataRoot, "Queue");
    public static string ClientRestoreDirectory => Path.Combine(ClientDataRoot, "Restore");
    public static string ClientLogsDirectory => Path.Combine(ClientDataRoot, "Logs");
    public static string ClientLogFilePath => Path.Combine(ClientLogsDirectory, "backup-agent.log");
    public static string ClientRunBackupSignalPath => Path.Combine(ClientDataRoot, "run-backup-now.signal");
    public static string ClientStopBackupSignalPath => Path.Combine(ClientDataRoot, "stop-backup.signal");

    public static readonly string[] DefaultBackupFolders =
    [
        "Desktop",
        "Documents",
        "Downloads",
        "Pictures",
        "Videos",
        "Music",
        "Favorites",
        "AppDataRoaming",
        "ChromeProfile",
        "EdgeProfile",
        "FirefoxProfile"
    ];

    public static readonly string[] DefaultExcludeDirectories =
    [
        "Temp",
        "Cache",
        "node_modules",
        ".git",
        ".vscode",
        ".idea",
        @"Microsoft\Teams\Cache",
        @"Google\Chrome\User Data\Default\Code Cache",
        @"Google\Chrome\User Data\Default\Cache",
        @"Google\Chrome\User Data\Default\GPUCache",
        @"Google\Chrome\User Data\GrShaderCache",
        @"Google\Chrome\User Data\ShaderCache",
        @"Google\Chrome\User Data\Crashpad",
        @"Google\Chrome\User Data\Service Worker\CacheStorage",
        @"Microsoft\Edge\User Data\Default\Code Cache",
        @"Microsoft\Edge\User Data\Default\Cache"
        ,@"Microsoft\Edge\User Data\Default\GPUCache"
        ,@"Microsoft\Edge\User Data\GrShaderCache"
        ,@"Microsoft\Edge\User Data\ShaderCache"
        ,@"Microsoft\Edge\User Data\Crashpad"
        ,@"Microsoft\Edge\User Data\Service Worker\CacheStorage"
        ,@"Mozilla\Firefox\Profiles\cache2"
        ,@"Mozilla\Firefox\Profiles\startupCache"
    ];

    public static readonly string[] DefaultExcludeFiles =
    [
        "*.tmp",
        "*.crdownload",
        "*.part",
        "*.log"
    ];

    public static readonly string[] KnownFolders =
    [
        "Desktop",
        "Documents",
        "Downloads",
        "Pictures",
        "Videos",
        "Music",
        "Favorites",
        "AppDataRoaming",
        "ChromeProfile",
        "EdgeProfile",
        "FirefoxProfile"
    ];
}
