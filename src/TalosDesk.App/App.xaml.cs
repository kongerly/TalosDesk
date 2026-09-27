using System.Configuration;
using System.Windows;
using System.Windows.Interop;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

public partial class App : Application
{
    private WorkspaceInstanceCoordinator? _instanceCoordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLaunchOptions options;
        try
        {
            options = AppLaunchOptions.Parse(e.Args);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "TalosDesk 启动参数无效", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var store = new WorkspaceStore(options.WorkspacePath);
        _instanceCoordinator = new WorkspaceInstanceCoordinator(store.FilePath);
        if (!_instanceCoordinator.IsPrimary)
        {
            _instanceCoordinator.SignalPrimaryInstance();
            Shutdown();
            return;
        }

        var window = new MainWindow(store, options.ProfileLabel);
        MainWindow = window;
        _instanceCoordinator.StartListening(() => Dispatcher.BeginInvoke(async () =>
        {
            await window.ReloadWorkspaceForSecondaryLaunchAsync();
            ActivateWindow(window);
        }));
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceCoordinator?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) NativeMethods.SetForegroundWindow(handle);
    }

}
