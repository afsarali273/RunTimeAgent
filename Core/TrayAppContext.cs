using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutomationAgent.Services;
using AutomationAgent.Utils;

namespace AutomationAgent.Core;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startMenuItem;
    private readonly ToolStripMenuItem _stopMenuItem;
    private readonly ToolStripMenuItem _restartMenuItem;
    private readonly RunnerService _runnerService;
    private readonly WatchdogService _watchdogService;
    private readonly ApiService _apiService;
    private readonly Logger _logger;
    private SynchronizationContext? _syncContext;
    private readonly Control _uiInvoker;
    private Icon? _robotIcon;
    private IntPtr _robotHIcon = IntPtr.Zero;

    public TrayAppContext(RunnerService runnerService, WatchdogService watchdogService, ApiService apiService, Logger logger)
    {
        _runnerService = runnerService ?? throw new ArgumentNullException(nameof(runnerService));
        _watchdogService = watchdogService ?? throw new ArgumentNullException(nameof(watchdogService));
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Defer creating the UI invoker control until the WinForms message loop starts.
        // We'll initialize it from the Application.Idle event so handle creation is on the UI thread.
        _syncContext = null;
        _uiInvoker = new Control();
        Application.Idle += OnApplicationIdle;

        _runnerService.StateChanged += Runner_StateChanged;

        _robotIcon = CreateRobotIcon(64);
        _notifyIcon = new NotifyIcon
        {
            Icon = _robotIcon,
            Text = "Automation Agent",
            Visible = true
        }; 

        _startMenuItem = new ToolStripMenuItem("Start Runner", null, (_, _) => ExecuteAsync(_runnerService.StartAsync));
        _stopMenuItem = new ToolStripMenuItem("Stop Runner", null, (_, _) => ExecuteAsync(_runnerService.StopAsync));
        _restartMenuItem = new ToolStripMenuItem("Restart Runner", null, (_, _) => ExecuteAsync(_runnerService.RestartAsync));
        var exitMenuItem = new ToolStripMenuItem("Exit", null, OnExitRequested);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { _startMenuItem, _stopMenuItem, _restartMenuItem, new ToolStripSeparator(), exitMenuItem });

        // Log and ensure menu reflects latest state when user opens it (prevents UI freezing from unexpected work)
        menu.Opening += (_, _) =>
        {
            try
            {
                _startMenuItem.Enabled = _runnerService.State != RunnerState.Running;
                _stopMenuItem.Enabled = _runnerService.State == RunnerState.Running;
                _restartMenuItem.Enabled = _runnerService.State != RunnerState.Stopped;
                _ = _logger.InfoAsync("Tray menu opening.");
            }
            catch (Exception ex)
            {
                _ = _logger.ErrorAsync("Tray menu Opening handler failed.", ex);
            }
        };

        menu.Closed += (_, _) => _ = _logger.InfoAsync("Tray menu closed.");
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.MouseClick += (_, e) => _ = _logger.InfoAsync($"NotifyIcon.MouseClick: {e.Button}");

        UpdateTray(_runnerService.State);
        GC.KeepAlive(_watchdogService);
        GC.KeepAlive(_apiService);
    }

    private void ExecuteAsync(Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                await _logger.ErrorAsync("Tray action failed.", ex);
            }
        });
    }

    private void Runner_StateChanged(object? sender, RunnerStateChangedEventArgs args)
    {
        // Prefer the UI control invoker (guarantees marshaling to the UI thread).
        if (_uiInvoker.IsHandleCreated)
        {
            _uiInvoker.BeginInvoke((Action)(() => UpdateTray(args.State)));
            return;
        }

        // If we have a captured SynchronizationContext (initialized on Application.Idle), post to it.
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => UpdateTray(args.State), null);
            return;
        }

        // Last-resort: log and update (best-effort) — this should be rare because the invoker is initialized
        // once the UI message loop starts via Application.Idle.
        _ = _logger.InfoAsync("Runner_StateChanged: UI invoker not ready; performing best-effort update.");
        try
        {
            UpdateTray(args.State);
        }
        catch (Exception ex)
        {
            _ = _logger.ErrorAsync("Failed to update tray state from background.", ex);
        }
    }

    private void UpdateTray(RunnerState state)
    {
        // Keep the custom robot icon, update tooltip to reflect current state.
        _notifyIcon.Text = $"Automation Agent ({state})";

        _startMenuItem.Enabled = state != RunnerState.Running;
        _stopMenuItem.Enabled = state == RunnerState.Running;
        _restartMenuItem.Enabled = state != RunnerState.Stopped;
    }

    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        // Initialize the UI invoker/control on the real WinForms UI thread.
        Application.Idle -= OnApplicationIdle;

        try
        {
            if (!_uiInvoker.IsHandleCreated)
            {
                _uiInvoker.CreateControl();
            }
        }
        catch (Exception ex)
        {
            _ = _logger.ErrorAsync("Failed to initialize UI invoker.", ex);
        }

        _syncContext = SynchronizationContext.Current;
    }

    private Icon CreateRobotIcon(int size = 64)
    {
        // Try to load a provided PNG from the Utils folder first (preferred).
        var file = Path.Combine(AppContext.BaseDirectory, "Utils", "robot.png");

        if (File.Exists(file))
        {
            try
            {
                using var src = new Bitmap(file);
                Bitmap bmp;

                if (src.Width == size && src.Height == size)
                {
                    bmp = new Bitmap(src);
                }
                else
                {
                    bmp = new Bitmap(size, size);
                    using var g = Graphics.FromImage(bmp);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.DrawImage(src, new Rectangle(0, 0, size, size));
                }

                IntPtr hIcon = bmp.GetHicon();
                _robotHIcon = hIcon;
                return Icon.FromHandle(hIcon);
            }
            catch (Exception ex)
            {
                _ = _logger.ErrorAsync("Failed to load Utils/robot.png — falling back to generated icon.", ex);
            }
        }

        // Fallback: programmatically draw a simple robot icon (existing behavior).
        var bmpFallback = new Bitmap(size, size);

        using (var g = Graphics.FromImage(bmpFallback))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int padding = Math.Max(2, size / 12);
            var headRect = new Rectangle(padding, padding + size / 12, size - padding * 2, size - padding * 3);

            using (var brush = new SolidBrush(Color.FromArgb(60, 130, 200)))
            using (var pen = new Pen(Color.FromArgb(20, 70, 120), Math.Max(2, size / 32)))
            {
                using var path = CreateRoundedRectangle(headRect, Math.Max(4, size / 8));
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            int eyeSize = Math.Max(4, size / 8);
            int eyeY = headRect.Top + headRect.Height / 4;
            int eyeX = headRect.Left + headRect.Width / 6;
            using (var white = new SolidBrush(Color.White))
            using (var black = new SolidBrush(Color.Black))
            {
                var leftEye = new Rectangle(eyeX, eyeY, eyeSize, eyeSize);
                var rightEye = new Rectangle(headRect.Right - eyeX - eyeSize, eyeY, eyeSize, eyeSize);
                g.FillEllipse(white, leftEye);
                g.FillEllipse(white, rightEye);
                g.FillEllipse(black, new Rectangle(leftEye.X + eyeSize / 4, leftEye.Y + eyeSize / 4, eyeSize / 2, eyeSize / 2));
                g.FillEllipse(black, new Rectangle(rightEye.X + eyeSize / 4, rightEye.Y + eyeSize / 4, eyeSize / 2, eyeSize / 2));
            }

            using (var mouth = new SolidBrush(Color.FromArgb(20, 20, 20)))
            {
                var mouthRect = new Rectangle(headRect.Left + headRect.Width / 6, headRect.Bottom - headRect.Height / 6, headRect.Width * 2 / 3, Math.Max(3, size / 18));
                g.FillRectangle(mouth, mouthRect);
            }

            using (var pen = new Pen(Color.FromArgb(20, 20, 20), Math.Max(2, size / 32)))
            {
                int centerX = size / 2;
                g.DrawLine(pen, centerX, headRect.Top - 2, centerX, headRect.Top - size / 8);
                g.FillEllipse(Brushes.Red, centerX - Math.Max(3, size / 32), headRect.Top - size / 8 - Math.Max(3, size / 32), Math.Max(6, size / 16), Math.Max(6, size / 16));
            }
        }

        IntPtr hIconFallback = bmpFallback.GetHicon();
        _robotHIcon = hIconFallback;
        return Icon.FromHandle(hIconFallback);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        // top-left
        path.AddArc(arc, 180, 90);

        // top-right
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);

        // bottom-right
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        // bottom-left
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _ = _logger.InfoAsync("Exit requested from tray menu.");
        ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnApplicationIdle;
            _runnerService.StateChanged -= Runner_StateChanged;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _uiInvoker.Dispose();

            if (_robotIcon is not null)
            {
                _robotIcon.Dispose();
                _robotIcon = null;
            }

            if (_robotHIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_robotHIcon);
                _robotHIcon = IntPtr.Zero;
            }
        }

        base.Dispose(disposing);
    }
}
