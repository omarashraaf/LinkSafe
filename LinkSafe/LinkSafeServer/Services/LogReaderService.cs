using Shared.Constants;

namespace LinkSafeServer.Services;

public sealed class LogReaderService(string? path = null)
{
    public string LogFilePath => path ?? LinkSafeDefaults.ServerLogFilePath;

    public async Task<IReadOnlyList<string>> ReadRecentLinesAsync(int maxLines = 200, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LogFilePath))
        {
            return [];
        }

        if (maxLines < 1) return [];
        // Bound each live read; never load an indefinitely growing log into memory.
        await using var stream = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var end = stream.Length;
        var start = Math.Max(0, end - 256 * 1024);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[(int)(end - start)];
        var read = 0;
        while (read < buffer.Length)
        {
            var amount = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (amount == 0) break;
            read += amount;
        }
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        if (text.Length == 0) return [];
        if (start > 0)
        {
            var newline = text.IndexOf('\n');
            text = newline < 0 ? "[Latest log line exceeds the live preview limit. Download the log to read it.]" : text[(newline + 1)..];
        }
        return text.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).TakeLast(Math.Min(maxLines, 200)).ToList();
    }
}
