namespace Shared.Models;

public sealed class BackupUploadResponse : OperationResult
{
    public int FilesProcessed { get; set; }
    public int FilesVersioned { get; set; }
}
