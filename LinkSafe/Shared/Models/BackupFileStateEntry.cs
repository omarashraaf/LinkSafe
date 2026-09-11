namespace Shared.Models;

public sealed class BackupFileStateEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public string ArchiveEntryPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
}
