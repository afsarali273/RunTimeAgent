using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutomationAgent.Utils;

namespace AutomationAgent.Services;

public sealed class ApiService : IDisposable
{
    private readonly RunnerService _runnerService;
    private readonly Logger _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenerTask;

    public ApiService(RunnerService runnerService, Logger logger)
    {
        _runnerService = runnerService ?? throw new ArgumentNullException(nameof(runnerService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _listener.Prefixes.Add("http://localhost:9000/");
        _listener.Start();
        _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
        _ = _logger.InfoAsync("HTTP API server listening on http://localhost:9000.");
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                await _logger.ErrorAsync("API listener failed.", ex);
                await Task.Delay(1000, cancellationToken);
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var response = context.Response;

        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? string.Empty;
            var method = request.HttpMethod;
            var normalizedPath = path.ToLowerInvariant();

            switch ((method, normalizedPath))
            {
                case ("GET", "/status"):
                    await WriteResponseAsync(response, new { status = _runnerService.State.ToString().ToLowerInvariant() });
                    break;
                case ("POST", "/start"):
                    await _runnerService.StartAsync();
                    await WriteResponseAsync(response, new { status = _runnerService.State.ToString().ToLowerInvariant() });
                    break;
                case ("POST", "/stop"):
                    await _runnerService.StopAsync();
                    await WriteResponseAsync(response, new { status = _runnerService.State.ToString().ToLowerInvariant() });
                    break;
                case ("POST", "/restart"):
                    await _runnerService.RestartAsync();
                    await WriteResponseAsync(response, new { status = _runnerService.State.ToString().ToLowerInvariant() });
                    break;
                default:
                    response.StatusCode = 404;
                    await WriteResponseAsync(response, new { error = "Not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync("API request failed.", ex);
            response.StatusCode = 500;
            await WriteResponseAsync(response, new { error = "Internal server error" });
        }
        finally
        {
            response.Close();
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _listener.Close();
        }
        catch
        {
            // ignored
        }

        try
        {
            _listenerTask.Wait();
        }
        catch (AggregateException)
        {
            // swallow cancellations
        }

        _cts.Dispose();
    }
}
