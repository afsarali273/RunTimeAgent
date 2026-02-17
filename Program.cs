using System;
using System.Windows.Forms;
using AutomationAgent.Core;
using AutomationAgent.Services;
using AutomationAgent.Utils;
using Microsoft.Win32;

namespace AutomationAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var logger = new Logger();
        using var runnerService = new RunnerService(logger);
        using var watchdogService = new WatchdogService(runnerService, logger);
        using var apiService = new ApiService(runnerService, logger);
        using var context = new TrayAppContext(runnerService, watchdogService, apiService, logger);

        EnsureAutoStart(logger);

        Application.Run(context);

        logger.InfoAsync("Application shutting down.").Wait();
    }

    private static void EnsureAutoStart(Logger logger)
    {
        try
        {
            var exePath = Application.ExecutablePath;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);

            if (key is not null)
            {
                key.SetValue("AutomationAgent", exePath);
                logger.InfoAsync("Auto-start registry key configured.").Wait();
            }
        }
        catch (Exception ex)
        {
            logger.ErrorAsync("Failed to configure auto-start.", ex).Wait();
        }
    }
}