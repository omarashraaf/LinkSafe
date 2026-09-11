using System.Text.Json;
using System.Threading;

namespace Shared.Storage;

public sealed class JsonFileStore<T>(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sync = Locks.GetOrAdd(Path.GetFullPath(filePath), _ => new(1, 1));

    public async Task<T> LoadAsync(Func<T> fallback)
    {
        return await LoadAsync(fallback, default);
    }

    public async Task<T> LoadAsync(Func<T> fallback, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectory();
            if (!File.Exists(filePath))
            {
                var value = fallback();
                await SaveInternalAsync(value);
                return value;
            }

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var valueFromDisk = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
            return valueFromDisk ?? fallback();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SaveAsync(T value) => await SaveAsync(value, default);

    public async Task<T> ReadAsync(Func<T> fallback, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken) ?? fallback();
        }
        catch (FileNotFoundException) { return fallback(); }
        catch (DirectoryNotFoundException) { return fallback(); }
        finally { _sync.Release(); }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            await SaveInternalAsync(value);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task WriteAsync(T value) => await SaveAsync(value, default);

    private async Task SaveInternalAsync(T value)
    {
        EnsureDirectory();
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
                }

                File.Move(tempPath, filePath, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt));
            }
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
