using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TalosDesk.App;

internal sealed class WindowPlacementController
{
    private readonly Window _window;
    private readonly WindowSettingsStore? _store;
    private WindowSettings _settings;
    private bool _ready;
    private bool _placing;

    private WindowPlacementController(Window window, WindowSettingsStore? store)
    {
        _window = window;
        _store = store;
        _settings = store?.Load() ?? new WindowSettings(window.Width, window.Height);
        window.SourceInitialized += (_, _) => Restore();
        window.Loaded += (_, _) => { _ready = true; Capture(); };
        window.LocationChanged += (_, _) => Capture();
        window.SizeChanged += (_, _) => Capture();
        window.StateChanged += (_, _) =>
        {
            if (!_placing && window.WindowState != WindowState.Minimized)
                _settings = _settings with { IsMaximized = window.WindowState == WindowState.Maximized };
            Capture();
        };
        window.Closed += (_, _) => _store?.Save(_settings);
    }

    internal static void Attach(Window window, WindowSettingsStore? store = null) =>
        _ = new WindowPlacementController(window, store);

    private void Restore()
    {
        _placing = true;
        try
        {
            var screens = ReadScreens();
            var ownerHandle = _window.Owner is null ? IntPtr.Zero : new WindowInteropHelper(_window.Owner).Handle;
            var ownerScreen = ownerHandle == IntPtr.Zero ? null : ReadScreen(MonitorFromWindow(ownerHandle, 2));
            var screen = _store is null && ownerScreen is not null ? ownerScreen :
                screens.FirstOrDefault(s => string.Equals(s.Name, _settings.MonitorName, StringComparison.OrdinalIgnoreCase)) ??
                screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
            _window.MinWidth = Math.Min(_window.MinWidth, screen.WorkArea.Width / screen.Scale);
            _window.MinHeight = Math.Min(_window.MinHeight, screen.WorkArea.Height / screen.Scale);
            var bounds = _settings.RestoreBounds(screen, new Size(_window.MinWidth, _window.MinHeight));
            _settings = _settings with
            {
                Width = bounds.Width / screen.Scale, Height = bounds.Height / screen.Scale,
                MonitorName = screen.Name,
                OffsetX = (bounds.Left - screen.WorkArea.Left) / screen.Scale,
                OffsetY = (bounds.Top - screen.WorkArea.Top) / screen.Scale
            };
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Width = _settings.Width;
            _window.Height = _settings.Height;
            SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero,
                (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
                (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0014);
            if (_settings.IsMaximized) _window.WindowState = WindowState.Maximized;
        }
        finally { _placing = false; }
    }

    private void Capture()
    {
        if (!_ready || _placing || _window.WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return;
        var screen = ReadScreen(MonitorFromWindow(handle, 2));
        var scale = GetDpiForWindow(handle) / 96d;
        if (screen is null || scale <= 0 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return;
        _settings = new WindowSettings((bounds.Right - bounds.Left) / scale, (bounds.Bottom - bounds.Top) / scale,
            false, screen.Name, (bounds.Left - screen.WorkArea.Left) / scale, (bounds.Top - screen.WorkArea.Top) / scale);
    }

    private static List<ScreenArea> ReadScreens()
    {
        var screens = new List<ScreenArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) =>
        {
            if (ReadScreen(monitor) is { } screen) screens.Add(screen);
            return true;
        }, IntPtr.Zero);
        if (screens.Count == 0) screens.Add(new ScreenArea("", SystemParameters.WorkArea, 1, true));
        return screens;
    }

    private static ScreenArea? ReadScreen(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96d : 1;
        var area = info.Work;
        return new ScreenArea(info.Device, new Rect(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top),
            scale > 0 ? scale : 1, (info.Flags & 1) != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
