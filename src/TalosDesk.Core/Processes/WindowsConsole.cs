using System.Runtime.InteropServices;

namespace TalosDesk.Core.Processes;

internal static class WindowsConsole
{
    private const uint CtrlCEvent = 0;
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);
    // 使用宿主专属处理器，避免 NULL/TRUE 的可继承忽略标记污染后续命令。
    // AttachConsole 会重置处理器表，因此每次附加后重新注册。
    private static readonly ManualResetEventSlim OwnCtrlCHandled = new(false);
    private static readonly ConsoleHandler IgnoreOwnCtrlC = eventType =>
    {
        if (eventType != CtrlCEvent) return false;
        OwnCtrlCHandled.Set();
        return true;
    };
    private static readonly IntPtr HandlerPointer = Marshal.GetFunctionPointerForDelegate(IgnoreOwnCtrlC);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ConsoleHandler(uint eventType);

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

            if (!WindowsNative.SetConsoleCtrlHandler(HandlerPointer, add: true)) return false;

            OwnCtrlCHandled.Reset();
            if (!WindowsNative.GenerateConsoleCtrlEvent(CtrlCEvent, 0)) return false;
            // 信号异步分发；收到宿主自己的回调后才能解除附加，避免移除处理器时仍有信号待投递。
            return OwnCtrlCHandled.Wait(TimeSpan.FromSeconds(2));
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
