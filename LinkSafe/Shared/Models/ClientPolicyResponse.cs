using Shared.Constants;

namespace Shared.Models;

public sealed class ClientPolicyResponse
{
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
    public bool IsClientOverride { get; set; }
}
