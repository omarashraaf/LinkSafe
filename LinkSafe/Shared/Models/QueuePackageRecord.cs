namespace Shared.Models;

public sealed class QueuePackageRecord
{
    public string PackageId { get; set; } = Guid.NewGuid().ToString("D");
    public string FilePath { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int RetryCount { get; set; }
    public string LastError { get; set; } = string.Empty;
    // Keep the JSON field name for compatibility; entries can now represent files or directories.
    public List<BackupFileStateEntry> IncludedFiles { get; set; } = [];
}
