using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutomationAgent.Utils;

public sealed class Logger : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly StreamWriter _writer;
    private bool _disposed;

    public Logger()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var logFile = Path.Combine(logDirectory, $"automation-agent-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        _ = InfoAsync("Logger initialized.");
    }

    private async Task LogAsync(string level, string message, Exception? exception = null)
    {
        if (_disposed)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append($"{DateTime.UtcNow:O} [{level}] {message}");

        if (exception is not null)
        {
            builder.Append(' ');
            builder.Append(exception);
        }

        var payload = builder.ToString();
        await _semaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            await _writer.WriteLineAsync(payload).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task InfoAsync(string message) => LogAsync("INFO", message);

    public Task ErrorAsync(string message, Exception? exception = null) => LogAsync("ERROR", message, exception);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _writer.Flush();
        _writer.Dispose();
        _semaphore.Dispose();
        _disposed = true;
    }
}
