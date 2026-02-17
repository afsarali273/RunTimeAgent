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

    // Singapore timezone helper (used for log filename and timestamps)
    private static readonly TimeZoneInfo SingaporeTimeZone = InitSingaporeTimeZone();

    private static TimeZoneInfo InitSingaporeTimeZone()
    {
        try
        {
            // Windows ID
            return TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                // Linux/macOS ID
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset SingaporeNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SingaporeTimeZone);

    public Logger()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var now = SingaporeNow();
        var logFile = Path.Combine(logDirectory, $"automation-agent-{now:yyyyMMdd-HHmmss}.log");
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
        builder.Append($"{SingaporeNow():O} [{level}] {message}");

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
