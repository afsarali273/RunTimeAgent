using System;
using System.Runtime.InteropServices;

namespace AutomationAgent.Utils;

internal static class NativeMethods
{
    [Flags]
    private enum ExecutionState : uint
    {
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    internal static void PreventSleep()
    {
        SetThreadExecutionState(ExecutionState.ES_CONTINUOUS | ExecutionState.ES_SYSTEM_REQUIRED | ExecutionState.ES_DISPLAY_REQUIRED);
    }
}
