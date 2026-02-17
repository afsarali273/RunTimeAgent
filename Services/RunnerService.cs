using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutomationAgent.Utils;

namespace AutomationAgent.Services;

public enum RunnerState
{
    Stopped,
    Running,
    Error
}

public sealed class RunnerStateChangedEventArgs : EventArgs
{
    public RunnerStateChangedEventArgs(RunnerState state) => State = state;

    public RunnerState State { get; }
}

public sealed class RunnerService : IDisposable
{
    private readonly Logger _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _stateLock = new();
    private RunnerState _state = RunnerState.Stopped;
    private Process? _process;
    private StreamWriter? _runnerLogWriter;
    private readonly object _runnerLogLock = new();
    private bool _disposed;

    public RunnerService(Logger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RunnerState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsRunning => State == RunnerState.Running;

    public event EventHandler<RunnerStateChangedEventArgs>? StateChanged;

    public async Task StartAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();

        try
        {
            await InternalStartAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();

        try
        {
            await InternalStopAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestartAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();

        try
        {
            await InternalStopAsync();
            await InternalStartAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task InternalStartAsync()
    {
        if (_process is { } running && !running.HasExited)
        {
            await _logger.InfoAsync("Runner already running when start requested.");
            return;
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "run.cmd");

        if (!File.Exists(scriptPath))
        {
            await _logger.ErrorAsync($"Runner script missing at {scriptPath}.");
            UpdateState(RunnerState.Error);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c run.cmd",
            WorkingDirectory = AppContext.BaseDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            ErrorDialog = false
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Exited += Process_Exited;

        // Capture runner stdout/stderr and write ONLY to the dedicated runner log (no app log duplication)
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                try
                {
                    lock (_runnerLogLock)
                    {
                        _runnerLogWriter?.WriteLine(e.Data);
                        _runnerLogWriter?.Flush();
                    }
                }
                catch
                {
                    // swallow runner-log write failures
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                try
                {
                    lock (_runnerLogLock)
                    {
                        _runnerLogWriter?.WriteLine($"ERR: {e.Data}");
                        _runnerLogWriter?.Flush();
                    }
                }
                catch
                {
                    // swallow runner-log write failures
                }
            }
        };


        try
        {
            // create a per-runner log file for easy debugging
            try
            {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);

                // Keep only the most recent N runner log files to limit disk use.
                const int maxRunnerLogs = 5;
                try
                {
                    var existing = new DirectoryInfo(logsDir).GetFiles("runner-*.log").OrderByDescending(f => f.CreationTimeUtc).ToArray();
                    if (existing.Length > maxRunnerLogs)
                    {
                        for (int i = maxRunnerLogs; i < existing.Length; i++)
                        {
                            try
                            {
                                existing[i].Delete();
                            }
                            catch { /* ignore deletion failures */ }
                        }
                    }
                }
                catch { /* ignore rotation errors */ }

                var runnerLogPath = Path.Combine(logsDir, $"runner-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
                _runnerLogWriter = new StreamWriter(new FileStream(runnerLogPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                _runnerLogWriter.WriteLine($"--- Runner log started {DateTime.UtcNow:O} ---");
            }
            catch (Exception ex)
            {
                // non-fatal: continue without dedicated runner file but log the error
                _ = _logger.ErrorAsync("Failed to create runner log file.", ex);
            }

            if (!process.Start())
            {
                throw new InvalidOperationException("Runner process did not start.");
            }

            // begin asynchronous read so OutputDataReceived/ErrorDataReceived fire
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _ = _logger.ErrorAsync("Failed to begin async reads from runner process.", ex);
            }

            _process = process;
            UpdateState(RunnerState.Running);
            await _logger.InfoAsync("Runner process started.");
        }
        catch (Exception ex)
        {
            try
            {
                _runnerLogWriter?.Dispose();
                _runnerLogWriter = null;
            }
            catch { }

            process.Exited -= Process_Exited;
            process.Dispose();
            await _logger.ErrorAsync("Failed to start runner process.", ex);
            UpdateState(RunnerState.Error);
        }
    }

    private async Task InternalStopAsync()
    {
        if (_process is null)
        {
            UpdateState(RunnerState.Stopped);
            return;
        }

        if (_process.HasExited)
        {
            _process.Exited -= Process_Exited;
            _process.Dispose();
            _process = null;

            try
            {
                _runnerLogWriter?.Dispose();
                _runnerLogWriter = null;
            }
            catch { }

            UpdateState(RunnerState.Stopped);
            return;
        }

        _process.Exited -= Process_Exited;

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync("Failed to stop runner process.", ex);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }

        UpdateState(RunnerState.Stopped);
        await _logger.InfoAsync("Runner process stopped.");
    }

    private void Process_Exited(object? sender, EventArgs args)
    {
        Task.Run(async () =>
        {
            await _operationLock.WaitAsync();

            try
            {
                _process?.Dispose();
                _process = null;

                try
                {
                    _runnerLogWriter?.Dispose();
                    _runnerLogWriter = null;
                }
                catch { }

                await _logger.InfoAsync("Runner process exited unexpectedly.");
                UpdateState(RunnerState.Stopped);
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    private void UpdateState(RunnerState newState)
    {
        RunnerState previous;

        lock (_stateLock)
        {
            previous = _state;

            if (previous == newState)
            {
                return;
            }

            _state = newState;
        }

        StateChanged?.Invoke(this, new RunnerStateChangedEventArgs(newState));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RunnerService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _operationLock.Dispose();
    }
}
