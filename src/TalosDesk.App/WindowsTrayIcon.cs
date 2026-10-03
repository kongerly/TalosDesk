using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TalosDesk.App;

internal sealed class WindowsTrayIcon : ITrayIcon
{
    private const int CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private HwndSource? _source;
    private IntPtr _icon;
    private NotifyIconData _data;
    private bool _registered;
    private bool _version4;
    private bool _disposed;

    public event Action? RestoreRequested;
    public event Action? ExitRequested;
    public event Action? RegistrationLost;

    public bool TryRegister(IntPtr windowHandle, string tooltip)
    {
        if (_disposed || windowHandle == IntPtr.Zero) return false;
        if (_registered) return true;
        if (_source is null)
        {
            _source = HwndSource.FromHwnd(windowHandle);
            if (_source is null) return false;
            _source.AddHook(WindowMessage);
        }
        if (_icon == IntPtr.Zero)
        {
            try { _icon = LoadIcon(windowHandle); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            { return false; }
            if (_icon == IntPtr.Zero) return false;
        }
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = windowHandle, Id = IconId,
            Flags = 0x01 | 0x02 | 0x04 | 0x80, Callback = CallbackMessage, Icon = _icon,
            Tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
            Info = string.Empty, InfoTitle = string.Empty, Version = 4
        };
        _registered = Shell_NotifyIcon(0, ref _data);
        _version4 = _registered && Shell_NotifyIcon(4, ref _data);
        return _registered;
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreated != 0 && (uint)message == _taskbarCreated)
        {
            _registered = false;
            RegistrationLost?.Invoke();
        }
        else if (message == CallbackMessage && _registered)
        {
            var notification = _version4 ? (int)(lParam.ToInt64() & 0xffff) : (int)lParam;
            var id = _version4 ? (uint)((lParam.ToInt64() >> 16) & 0xffff) : (uint)wParam.ToInt64();
            if (id != IconId) return IntPtr.Zero;
            handled = true;
            if (notification is 0x400 or 0x401 || (!_version4 && notification == 0x202))
                QueueAction(() => RestoreRequested?.Invoke());
            else if (notification == 0x7b || (!_version4 && notification == 0x205))
                ShowMenu(wParam);
        }
        return IntPtr.Zero;
    }

    private void QueueAction(Action action) => _source?.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_disposed) action();
    }));

    private void ShowMenu(IntPtr position)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            if (!AppendMenu(menu, 0, 1, "显示主窗口") || !AppendMenu(menu, 0, 2, "退出")) return;
            var point = new NativePoint
            {
                X = (short)(position.ToInt64() & 0xffff),
                Y = (short)((position.ToInt64() >> 16) & 0xffff)
            };
            if (!_version4 || (point.X == -1 && point.Y == -1)) GetCursorPos(out point);
            NativeMethods.SetForegroundWindow(_data.Window);
            var choice = TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x02, point.X, point.Y, _data.Window, IntPtr.Zero);
            PostMessage(_data.Window, 0, IntPtr.Zero, IntPtr.Zero);
            if (choice == 1) QueueAction(() => RestoreRequested?.Invoke());
            else if (choice == 2) QueueAction(() => ExitRequested?.Invoke());
            else Shell_NotifyIcon(3, ref _data);
        }
        finally { DestroyMenu(menu); }
    }

    private static IntPtr LoadIcon(IntPtr window)
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/TalosDesk.App;component/Assets/TalosDeskMark.ico"));
        if (resource is null) return IntPtr.Zero;
        using var stream = resource.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length < 6) return IntPtr.Zero;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var desired = GetSystemMetricsForDpi(49, GetDpiForWindow(window));
        var bestScore = int.MaxValue;
        var bestOffset = 0;
        var bestLength = 0;
        for (var i = 0; i < count && 6 + (i + 1) * 16 <= bytes.Length; i++)
        {
            var entry = bytes.AsSpan(6 + i * 16, 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            var score = Math.Abs(width - desired) + (width < desired ? 256 : 0);
            if (length <= 0 || offset < 0 || (long)offset + length > bytes.Length || score >= bestScore) continue;
            bestScore = score;
            bestOffset = offset;
            bestLength = length;
        }
        if (bestLength == 0) return IntPtr.Zero;
        return CreateIconFromResourceEx(bytes.AsSpan(bestOffset, bestLength).ToArray(), (uint)bestLength,
            true, 0x00030000, desired, desired, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered) Shell_NotifyIcon(2, ref _data);
        _registered = false;
        _source?.RemoveHook(WindowMessage);
        _source = null;
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] bytes, uint length,
        [MarshalAs(UnmanagedType.Bool)] bool isIcon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
