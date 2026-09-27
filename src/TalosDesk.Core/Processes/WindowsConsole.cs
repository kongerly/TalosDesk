using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TalosDesk.Core.Processes;

internal static class WindowsConsole
{
    private const uint CtrlCEvent = 0;
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    static WindowsConsole()
    {
        // The application is a GUI process. Keep it immune to the control event sent to a command's isolated console.
        WindowsNative.SetConsoleCtrlHandler(IntPtr.Zero, add: true);
    }

    public static bool TrySendCtrlC(int processId)
    {
        ConsoleLock.Wait();
        var hadConsole = WindowsNative.GetConsoleWindow() != IntPtr.Zero;
        try
        {
            WindowsNative.FreeConsole();
            if (!WindowsNative.AttachConsole((uint)processId))
            {
                return false;
            }

            return WindowsNative.GenerateConsoleCtrlEvent(CtrlCEvent, 0);
        }
        finally
        {
            WindowsNative.FreeConsole();
            if (hadConsole)
            {
                WindowsNative.AttachConsole(AttachParentProcess);
            }

            ConsoleLock.Release();
        }
    }
}
