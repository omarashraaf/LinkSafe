namespace Shared.Storage;

public static class AtomicBackupFile
{
    public static async Task WriteAsync(Stream source, string stagingPath, string destinationPath,
        long expectedLength, DateTime timestamp, Func<Task> beforeReplace, CancellationToken cancellationToken)
    {
        try
        {
            await using (var staged = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(staged, cancellationToken);
                if (staged.Length != expectedLength) throw new InvalidDataException("Incomplete ZIP entry.");
            }
            File.SetLastWriteTimeUtc(stagingPath, timestamp);
            cancellationToken.ThrowIfCancellationRequested();
            await beforeReplace();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, destinationPath, true);
        }
        finally { File.Delete(stagingPath); }
    }
}
