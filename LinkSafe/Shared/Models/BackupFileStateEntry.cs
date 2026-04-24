namespace Shared.Models;

public sealed class BackupFileStateEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
}
