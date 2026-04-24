using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Shared.Logging;

public sealed class FileLoggerProvider(string filePath, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, filePath, minimumLevel));

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger(string categoryName, string filePath, LogLevel minimumLevel) : ILogger
    {
        private static readonly SemaphoreSlim Semaphore = new(1, 1);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {categoryName}: {message}";
            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            Semaphore.Wait();
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Avoid crashing the host if another process temporarily holds the log file.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore transient access issues and keep the agent/server running.
            }
            finally
            {
                Semaphore.Release();
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
