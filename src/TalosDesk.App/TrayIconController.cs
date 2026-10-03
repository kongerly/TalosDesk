using System.Windows;
using System.Windows.Interop;

namespace TalosDesk.App;

internal interface ITrayIcon : IDisposable
{
    event Action? RestoreRequested;
    event Action? ExitRequested;
    event Action? RegistrationLost;
    bool TryRegister(IntPtr windowHandle, string tooltip);
}

internal sealed class TrayIconController : IDisposable
{
    private readonly Window _window;
    private readonly ITrayIcon _icon;
    private readonly string _tooltip;
    private WindowState _restoreState;
    private bool _registered;
    private bool _restoring;
    private bool _disposed;
    private Rect _restoreBounds = Rect.Empty;

    internal TrayIconController(Window window, string profileLabel, ITrayIcon icon)
    {
        _window = window;
        _icon = icon;
        _tooltip = $"TalosDesk · {profileLabel}";
        _restoreState = window.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        window.SourceInitialized += SourceInitialized;
        window.StateChanged += StateChanged;
        window.Closed += Closed;
        icon.RestoreRequested += RestoreWindow;
        icon.ExitRequested += RequestExit;
        icon.RegistrationLost += RegistrationLost;
    }

    private void SourceInitialized(object? sender, EventArgs args) => Register();

    private bool Register() => _registered = _icon.TryRegister(new WindowInteropHelper(_window).Handle, _tooltip);

    private void StateChanged(object? sender, EventArgs args)
    {
        if (_disposed || _restoring) return;
        if (_window.WindowState != WindowState.Minimized)
        {
            _restoreState = _window.WindowState;
            return;
        }
        // Hide 会让 WPF 记录最小化停放坐标，因此先保留普通窗口边界。
        _restoreBounds = _window.RestoreBounds;
        // 注册失败时保留普通最小化窗口，确保用户仍可从任务栏找回应用。
        if (_registered || Register()) _window.Hide();
    }

    internal void RestoreWindow()
    {
        if (_disposed) return;
        _restoring = true;
        try
        {
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
                if (!_restoreBounds.IsEmpty)
                {
                    _window.Left = _restoreBounds.Left;
                    _window.Top = _restoreBounds.Top;
                    _window.Width = _restoreBounds.Width;
                    _window.Height = _restoreBounds.Height;
                }
                _window.WindowState = _restoreState;
            }
            _window.Activate();
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero) NativeMethods.SetForegroundWindow(handle);
        }
        finally { _restoring = false; }
    }

    private void RequestExit()
    {
        if (_disposed) return;
        RestoreWindow();
        _window.Close();
    }

    private void RegistrationLost()
    {
        if (_disposed) return;
        _registered = false;
        if (!Register() && !_window.IsVisible) RestoreWindow();
    }

    private void Closed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= SourceInitialized;
        _window.StateChanged -= StateChanged;
        _window.Closed -= Closed;
        _icon.RestoreRequested -= RestoreWindow;
        _icon.ExitRequested -= RequestExit;
        _icon.RegistrationLost -= RegistrationLost;
        _icon.Dispose();
    }
}
