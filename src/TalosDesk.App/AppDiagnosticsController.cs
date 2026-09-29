using TalosDesk.Core.Diagnostics;

namespace TalosDesk.App;

internal sealed class AppDiagnosticsController
{
    private readonly CrashRecordStore _recordStore;
    private readonly string _version;
    private readonly string _channel;
    private readonly Action<int> _terminate;
    private int _fatalStarted;
    private int _unobservedRecorded;

    public AppDiagnosticsController(
        CrashRecordStore recordStore,
        DiagnosticSettingsResult settings,
        string version,
        string channel,
        Action<int>? terminate = null)
    {
        _recordStore = recordStore;
        IsEnabled = settings.Status == DiagnosticSettingsStatus.Available && settings.Settings.IsEnabled;
        _version = version;
        _channel = channel;
        _terminate = terminate ?? Environment.Exit;
    }

    public bool IsEnabled { get; set; }

    public void RecordFatal(Exception exception, CrashSource source)
    {
        if (Interlocked.Exchange(ref _fatalStarted, 1) != 0) return;
        _recordStore.Write(exception, source, true, _version, _channel, IsEnabled);
        _terminate(1);
    }

    public void RecordUnobserved(Exception exception)
    {
        if (Volatile.Read(ref _fatalStarted) != 0 || Interlocked.Exchange(ref _unobservedRecorded, 1) != 0) return;
        _recordStore.Write(exception, CrashSource.UnobservedTask, false, _version, _channel, IsEnabled);
    }
}
