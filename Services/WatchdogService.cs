using System;
using System.Threading;
using System.Threading.Tasks;
using AutomationAgent.Utils;

namespace AutomationAgent.Services;

public sealed class WatchdogService : IDisposable
{
    private readonly RunnerService _runnerService;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _monitorTask;

    public WatchdogService(RunnerService runnerService, Logger logger)
    {
        _runnerService = runnerService ?? throw new ArgumentNullException(nameof(runnerService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                NativeMethods.PreventSleep();

                if (!_runnerService.IsRunning)
                {
                    await _logger.InfoAsync("Watchdog detected runner not running. Restarting.");
                    await _runnerService.RestartAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await _logger.ErrorAsync("Watchdog loop encountered an error.", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _monitorTask.Wait();
        }
        catch (AggregateException)
        {
            // swallow task cancellation
        }

        _cts.Dispose();
    }
}
