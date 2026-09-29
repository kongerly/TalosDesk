using System.Configuration;
using System.Windows;
using System.Windows.Interop;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Diagnostics;

namespace TalosDesk.App;

public partial class App : Application
{
    private WorkspaceInstanceCoordinator? _instanceCoordinator;
    private AppDiagnosticsController? _diagnostics;

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
        var buildInfo = ApplicationBuildInfo.Read(typeof(App).Assembly);
        var diagnosticSettingsStore = new DiagnosticSettingsStore(store.FilePath);
        var diagnosticSettings = diagnosticSettingsStore.Load();
        var crashRecordStore = new CrashRecordStore(store.FilePath);
        _diagnostics = new AppDiagnosticsController(
            crashRecordStore,
            diagnosticSettings,
            buildInfo.DisplayVersion,
            buildInfo.Channel?.ToString() ?? "Unknown");
        RegisterDiagnosticHandlers(this, _diagnostics);

        _instanceCoordinator = new WorkspaceInstanceCoordinator(store.FilePath);
        if (!_instanceCoordinator.IsPrimary)
        {
            _instanceCoordinator.SignalPrimaryInstance();
            Shutdown();
            return;
        }

        var window = new MainWindow(store, options.ProfileLabel, null, buildInfo, null,
            crashRecordStore, diagnosticSettingsStore, _diagnostics);
        MainWindow = window;
        _instanceCoordinator.StartListening(() => Dispatcher.BeginInvoke(async () =>
        {
            await window.ReloadWorkspaceForSecondaryLaunchAsync();
            ActivateWindow(window);
        }));
        window.Show();
    }

    internal static void RegisterDiagnosticHandlers(Application application, AppDiagnosticsController diagnostics)
    {
        application.DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            diagnostics.RecordFatal(args.Exception, CrashSource.Dispatcher);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ?? new InvalidOperationException();
            diagnostics.RecordFatal(exception, CrashSource.AppDomain);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => diagnostics.RecordUnobserved(args.Exception);
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
