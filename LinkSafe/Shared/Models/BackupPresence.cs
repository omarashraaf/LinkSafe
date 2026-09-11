namespace Shared.Models;

public sealed class BackupPresenceRequest
{
    public const int BatchSize = 500;
    public string DomainUser { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public List<BackupPresenceEntry> Files { get; set; } = [];
}

public sealed class BackupPresenceEntry
{
    public bool IsDirectory { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
}

public sealed class BackupPresenceResponse
{
    public bool SupportsDirectories { get; set; }
    public List<string> PresentPaths { get; set; } = [];
}
