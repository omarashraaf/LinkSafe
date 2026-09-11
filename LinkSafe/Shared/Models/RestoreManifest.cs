namespace Shared.Models;

public sealed class RestoreManifest
{
    public List<RestoreFileEntry> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class RestoreFileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
}
