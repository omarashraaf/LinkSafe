namespace Shared.Models;

public sealed class BackupStateFile
{
    public List<BackupFileStateEntry> Files { get; set; } = [];
}
