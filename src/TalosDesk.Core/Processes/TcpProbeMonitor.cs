using System.Net;
using System.Net.Sockets;
using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Processes;

public enum TcpProbeState { NotConfigured, Inactive, Waiting, Passed, TimedOut, Unreachable }

public sealed record TcpProbeSnapshot(TcpProbeState State, int ConsecutiveFailures, DateTimeOffset? LastCheckedAt);

internal interface ITcpProbeConnector
{
    Task ConnectAsync(string address, int port, CancellationToken cancellationToken);
}

internal sealed class TcpProbeConnector : ITcpProbeConnector
{
    public async Task ConnectAsync(string address, int port, CancellationToken cancellationToken)
    {
        var ip = IPAddress.Parse(address);
        using var client = new TcpClient(ip.AddressFamily);
        await client.ConnectAsync(ip, port, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class TcpProbeMonitor : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly TcpProbeConfiguration _configuration;
    private readonly ITcpProbeConnector _connector;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenSource _deadlineCancellation = new();
    private readonly Task _loop;
    private readonly Task _deadline;
    private readonly long _started;
    private bool _active = true;
    private bool _hasPassed;
    private TcpProbeSnapshot _snapshot = new(TcpProbeState.Waiting, 0, null);

    internal TcpProbeMonitor(TcpProbeConfiguration configuration, ITcpProbeConnector connector, TimeProvider time)
    {
        configuration.Validate();
        _configuration = configuration.Clone();
        _connector = connector;
        _time = time;
        _started = time.GetTimestamp();
        _deadline = WatchDeadlineAsync();
        _loop = ProbeAsync();
    }

    public TcpProbeSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public event EventHandler<TcpProbeSnapshot>? Changed;

    public void Stop()
    {
        lock (_sync)
        {
            if (!_active) return;
            _active = false;
            Publish(_snapshot with { State = TcpProbeState.Inactive });
        }
        _cancellation.Cancel();
        _deadlineCancellation.Cancel();
    }

    private void Publish(TcpProbeSnapshot snapshot)
    {
        // 调用者持有状态锁；取消和结果发布在同一边界串行化。
        _snapshot = snapshot;
        try { Changed?.Invoke(this, snapshot); }
        catch { /* 界面订阅失败不能终止探测或进程。 */ }
    }

    private async Task WatchDeadlineAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_configuration.StartupTimeoutSeconds), _time, _deadlineCancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_active && !_hasPassed) Publish(_snapshot with { State = TcpProbeState.TimedOut });
            }
        }
        catch (OperationCanceledException) when (_deadlineCancellation.IsCancellationRequested) { }
    }

    private async Task ProbeAsync()
    {
        var token = _cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool passed;
                using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    try
                    {
                        await _connector.ConnectAsync(_configuration.Address, _configuration.Port, attempt.Token)
                            .WaitAsync(TimeSpan.FromSeconds(_configuration.ConnectTimeoutSeconds), _time, token).ConfigureAwait(false);
                        passed = true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { passed = false; }
                    finally { attempt.Cancel(); }
                }

                lock (_sync)
                {
                    if (!_active) return;
                    if (!_hasPassed && _time.GetElapsedTime(_started) >= TimeSpan.FromSeconds(_configuration.StartupTimeoutSeconds))
                        Publish(_snapshot with { State = TcpProbeState.TimedOut });
                    if (passed)
                    {
                        _hasPassed = true;
                        _deadlineCancellation.Cancel();
                        Publish(new(TcpProbeState.Passed, 0, _time.GetUtcNow()));
                    }
                    else
                    {
                        var failures = Math.Min(_snapshot.ConsecutiveFailures + 1, _configuration.FailureThreshold);
                        var state = _hasPassed
                            ? failures >= _configuration.FailureThreshold ? TcpProbeState.Unreachable : TcpProbeState.Passed
                            : _snapshot.State;
                        Publish(new(state, failures, _time.GetUtcNow()));
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(_configuration.IntervalSeconds), _time, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.WhenAll(_loop, _deadline).ConfigureAwait(false);
        _cancellation.Dispose();
        _deadlineCancellation.Dispose();
    }
}
