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

    private readonly SemaphoreSlim _sync = new(1, 1);

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

            await using var stream = File.OpenRead(filePath);
            var valueFromDisk = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
            return valueFromDisk ?? fallback();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SaveAsync(T value) => await SaveAsync(value, default);

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
        var tempPath = $"{filePath}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
        }

        File.Move(tempPath, filePath, true);
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