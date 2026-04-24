using Shared.Constants;

namespace LinkSafeServer.Services;

public sealed class LogReaderService
{
    public string LogFilePath => LinkSafeDefaults.ServerLogFilePath;

    public async Task<IReadOnlyList<string>> ReadRecentLinesAsync(int maxLines = 200, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LogFilePath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(LogFilePath, cancellationToken);
        return lines.TakeLast(maxLines).ToList();
    }
}
