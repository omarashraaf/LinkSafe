using Shared.Constants;

namespace Shared.Models;

public sealed class ClientAgentConfig
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesLocalFolderSelection { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = LinkSafeDefaults.DefaultServerUrl;
    public bool IsAgentEnabled { get; set; }
    public bool Registered { get; set; }
    public string UserFolderName { get; set; } = string.Empty;
    public string ComputerFolderName { get; set; } = string.Empty;
    public string BackupDestinationId { get; set; } = string.Empty;
    public List<string> BackupFolders { get; set; } = [.. LinkSafeDefaults.DefaultBackupFolders];
    public List<string> CustomFolders { get; set; } = [];
    public List<string> ProtectedSourcePaths { get; set; } = [];
    public List<string> ExcludeDirectories { get; set; } = [.. LinkSafeDefaults.DefaultExcludeDirectories];
    public List<string> ExcludeFiles { get; set; } = [.. LinkSafeDefaults.DefaultExcludeFiles];
    public int BackupIntervalMinutes { get; set; } = 30;
    public int DelayBeforeCopySeconds { get; set; } = 60;
    public int StableFileCheckSeconds { get; set; } = 10;
    public int MaxFileSizeMB { get; set; } = 500;
    public int MaxUploadSizeMB { get; set; } = 1000;
}
