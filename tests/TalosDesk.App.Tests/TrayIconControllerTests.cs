using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TalosDesk.App.Tests;

internal static class TrayIconControllerTests
{
    // WPF 在一个进程中只能创建一次 Application，场景共用现有桌面集成宿主。
    internal static void Run()
    {
        MinimizeAndRestorePreserveNormalBoundsAndMaximizedState();
        FailedRegistrationKeepsTaskbarWindowAndCanRetry();
        ShellRestartReregistersOrRestoresHiddenWindow();
        TrayExitRestoresBeforeClosingAndCanceledExitKeepsIcon();
        if (Environment.GetEnvironmentVariable("TALOSDESK_NATIVE_TRAY_TEST") == "1")
            NativeIconRegistersAndIsReleased();
    }

    private static void NativeIconRegistersAndIsReleased()
    {
        var window = new Window { Title = "隔离原生托盘验证", Width = 500, Height = 300 };
        using var icon = new WindowsTrayIcon();
        using var controller = new TrayIconController(window, "隔离原生托盘验证", icon);
        try
        {
            window.Show();
            Pump();
            var handle = new WindowInteropHelper(window).Handle;
            Assert.IsTrue(icon.TryRegister(handle, "TalosDesk · 隔离原生托盘验证"),
                "启用原生托盘验收时必须有可用的 Windows 通知区域。");
            window.WindowState = WindowState.Minimized;
            Assert.IsFalse(window.IsVisible);
            controller.RestoreWindow();
            Pump();
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            window.Close();
            Assert.IsFalse(icon.TryRegister(handle, "已关闭的隔离测试"));
        }
        finally { controller.Dispose(); if (window.IsVisible) window.Close(); }
    }

    private static void MinimizeAndRestorePreserveNormalBoundsAndMaximizedState()
    {
        var window = new Window { Width = 500, Height = 300 };
        var icon = new TestTrayIcon();
        using var controller = new TrayIconController(window, "隔离托盘测试", icon);
        try
        {
            window.Show();
            Pump();
            Assert.AreEqual("TalosDesk · 隔离托盘测试", icon.Tooltip);
            Assert.AreNotEqual(IntPtr.Zero, icon.WindowHandle);
            var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            for (var i = 0; i < 3; i++)
            {
                window.WindowState = WindowState.Minimized;
                Pump();
                Assert.IsFalse(window.IsVisible);
                icon.Restore();
                Pump();
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(WindowState.Normal, window.WindowState);
                Assert.AreEqual(bounds, new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight));
            }
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            Assert.IsFalse(window.IsVisible);
            controller.RestoreWindow();
            Pump();
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(1, icon.RegisterCount, "反复恢复不应增加托盘图标。");
        }
        finally { window.Close(); }
        Assert.AreEqual(1, icon.DisposeCount);
        icon.Restore();
        controller.Dispose();
        Assert.AreEqual(1, icon.DisposeCount);
    }

    private static void FailedRegistrationKeepsTaskbarWindowAndCanRetry()
    {
        var window = new Window();
        var icon = new TestTrayIcon { CanRegister = false };
        using var controller = new TrayIconController(window, "隔离托盘测试", icon);
        try
        {
            window.Show();
            window.WindowState = WindowState.Minimized;
            Assert.IsTrue(window.IsVisible, "图标不可用时不能隐藏唯一的窗口入口。");
            Assert.IsTrue(window.ShowInTaskbar);
            Assert.AreEqual(WindowState.Minimized, window.WindowState);
            controller.RestoreWindow();
            icon.CanRegister = true;
            window.WindowState = WindowState.Minimized;
            Assert.IsFalse(window.IsVisible);
        }
        finally { window.Close(); }
    }

    private static void ShellRestartReregistersOrRestoresHiddenWindow()
    {
        var window = new Window();
        var icon = new TestTrayIcon();
        using var controller = new TrayIconController(window, "隔离托盘测试", icon);
        try
        {
            window.Show();
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            icon.LoseRegistration();
            Assert.AreEqual(2, icon.RegisterCount);
            Assert.IsFalse(window.IsVisible);
            icon.CanRegister = false;
            icon.LoseRegistration();
            Pump();
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
        }
        finally { window.Close(); }
    }

    private static void TrayExitRestoresBeforeClosingAndCanceledExitKeepsIcon()
    {
        var window = new Window();
        var icon = new TestTrayIcon();
        using var controller = new TrayIconController(window, "隔离托盘测试", icon);
        var closeCount = 0;
        CancelEventHandler cancel = (_, args) =>
        {
            closeCount++;
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            args.Cancel = true;
        };
        try
        {
            window.Show();
            window.Closing += cancel;
            window.WindowState = WindowState.Minimized;
            icon.Exit();
            Assert.AreEqual(1, closeCount);
            Assert.AreEqual(0, icon.DisposeCount);
            window.Closing -= cancel;
            window.WindowState = WindowState.Minimized;
            icon.Exit();
            Assert.AreEqual(1, icon.DisposeCount);
        }
        finally
        {
            window.Closing -= cancel;
            if (icon.DisposeCount == 0) window.Close();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

internal sealed class TestTrayIcon : ITrayIcon
{
    public event Action? RestoreRequested;
    public event Action? ExitRequested;
    public event Action? RegistrationLost;
    internal bool CanRegister { get; set; } = true;
    internal int RegisterCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal string? Tooltip { get; private set; }
    internal IntPtr WindowHandle { get; private set; }

    public bool TryRegister(IntPtr windowHandle, string tooltip)
    {
        RegisterCount++;
        WindowHandle = windowHandle;
        Tooltip = tooltip;
        return CanRegister;
    }

    internal void Restore() => RestoreRequested?.Invoke();
    internal void Exit() => ExitRequested?.Invoke();
    internal void LoseRegistration() => RegistrationLost?.Invoke();
    public void Dispose() => DisposeCount++;
}
