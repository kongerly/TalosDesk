using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TalosDesk.App;

internal sealed class WorkspaceInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _stopEvent = new(false, EventResetMode.ManualReset);
    private readonly bool _ownsMutex;
    private Task? _listenerTask;
    private bool _disposed;

    public WorkspaceInstanceCoordinator(string workspacePath)
    {
        var normalizedPath = Path.GetFullPath(workspacePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        _mutex = new Mutex(initiallyOwned: false, $"Local\\TalosDesk.Workspace.{hash}");
        _activationEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, $"Local\\TalosDesk.Activate.{hash}");
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
    }

    public bool IsPrimary => _ownsMutex;

    public void SignalPrimaryInstance()
    {
        if (!_ownsMutex) _activationEvent.Set();
    }

    public void StartListening(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        if (!_ownsMutex || _listenerTask is not null) return;

        _listenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _activationEvent, _stopEvent };
            while (WaitHandle.WaitAny(handles) == 0) activate();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopEvent.Set();
        try { _listenerTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        if (_ownsMutex) _mutex.ReleaseMutex();
        _activationEvent.Dispose();
        _stopEvent.Dispose();
        _mutex.Dispose();
    }
}
