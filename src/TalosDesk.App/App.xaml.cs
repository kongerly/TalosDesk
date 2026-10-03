using System.Configuration;
using System.Windows;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Diagnostics;

namespace TalosDesk.App;

public partial class App : Application
{
    private readonly bool _launchWorkspace;

    public App() : this(true) { }

    // 桌面测试自行创建隔离窗口，不能解析测试宿主参数或打开正式工作区。
    internal App(bool launchWorkspace) => _launchWorkspace = launchWorkspace;

    private WorkspaceInstanceCoordinator? _instanceCoordinator;
    private AppDiagnosticsController? _diagnostics;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_launchWorkspace) return;

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
            window.RestoreWindow();
            await window.ReloadWorkspaceForSecondaryLaunchAsync();
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

}
