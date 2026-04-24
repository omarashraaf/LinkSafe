namespace Shared.Models;

public sealed class BackupBrowseItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastModified { get; set; }
}
