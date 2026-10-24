using System.ComponentModel;
using System.Runtime.InteropServices;

if (args.Length != 1) return 64;
var progress = Path.GetFullPath(args[0]);
File.WriteAllText(progress, "host-started");
Console.WriteLine("phase:ignore-host-started");
if (!Native.SetConsoleCtrlHandler(IntPtr.Zero, true))
    throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic Ctrl+C handler registration failed.");
File.WriteAllText(progress, $"handler-registered:{Environment.ProcessId}");
Console.WriteLine("phase:ignore-handler-registered");
Console.WriteLine($"child:{Environment.ProcessId}");
Thread.Sleep(Timeout.Infinite);
return 0;

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
