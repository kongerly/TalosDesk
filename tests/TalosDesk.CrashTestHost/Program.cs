using System.IO;
using System.Windows;
using System.Windows.Threading;
using TalosDesk.App;
using TalosDesk.Core.Diagnostics;
using TalosDesk.Core.Processes;

if (args.Length != 3) Environment.Exit(64);
var workspace = Path.GetFullPath(args[0]);
var mode = args[1];
var pidFile = Path.GetFullPath(args[2]);
var directory = Path.GetDirectoryName(workspace)!;
Directory.CreateDirectory(directory);

var escapedPidFile = pidFile.Replace("'", "''", StringComparison.Ordinal);
var runner = new CommandRunner();
var session = runner.Start(Guid.NewGuid(),
    $"Set-Content -LiteralPath '{escapedPidFile}' -Value $PID; while ($true) {{ Start-Sleep -Milliseconds 100 }}",
    directory);
var deadline = DateTime.UtcNow.AddSeconds(15);
while (!File.Exists(pidFile) && DateTime.UtcNow < deadline) Thread.Sleep(20);
if (!File.Exists(pidFile)) Environment.Exit(65);

var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
var store = new CrashRecordStore(workspace);
var settings = new DiagnosticSettingsResult(new DiagnosticSettings(), DiagnosticSettingsStatus.Available, true);
var controller = new AppDiagnosticsController(store, settings, "0.2.0", "Preview");
App.RegisterDiagnosticHandlers(application, controller);

if (string.Equals(mode, "dispatcher", StringComparison.Ordinal))
{
    _ = application.Dispatcher.BeginInvoke(() => throw new InvalidOperationException("synthetic-secret-dispatcher"),
        DispatcherPriority.Normal);
}
else if (string.Equals(mode, "background", StringComparison.Ordinal))
{
    var thread = new Thread(() => throw new InvalidOperationException("synthetic-secret-background"));
    thread.Start();
}
else Environment.Exit(66);

application.Run();
