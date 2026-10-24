using System.Collections.Concurrent;
using System.Text;
using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Processes;

public enum CommandRunState
{
    Running,
    Succeeded,
    Failed,
    Stopped
}

public sealed record CommandOutput(DateTimeOffset Timestamp, string Stream, string Text);

public sealed record CommandRunResult(CommandRunState State, int ExitCode, bool WasForceTerminated);

public enum CommandStopResult
{
    AlreadyExited,
    StoppedAfterCtrlC,
    ForceTerminated
}

public sealed class CommandRunSession : ICommandRunSession
{
    private readonly SafeJobHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly int _processId;
    private readonly ICommandStopOperations _stopOperations;
    private readonly IReadOnlyList<string> _sensitiveValues;
    private readonly ConcurrentQueue<CommandOutput> _output = new();
    private readonly Task<CommandRunResult> _completion;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _outputCount;
    private int _stopRequested;
    private int _forceTerminated;
    private readonly TcpProbeMonitor? _probe;
    private int _stopInProgress;
    private int _ownershipReleased;
    private readonly ICommandObservationOperations _observation;
    private Task _outputDrain = Task.CompletedTask;

    internal CommandRunSession(SafeJobHandle job, SafeProcessHandle process, int processId, StreamReader stdout, StreamReader stderr,
        EventHandler<CommandOutput>? outputReceived, ICommandStopOperations stopOperations, IReadOnlyList<string> sensitiveValues,
        TcpProbeConfiguration? tcpProbe = null, ICommandObservationOperations? observation = null)
    {
        _job = job;
        _process = process;
        _processId = processId;
        _stopOperations = stopOperations;
        _sensitiveValues = sensitiveValues;
        _observation = observation ?? WindowsCommandObservationOperations.Instance;
        if (outputReceived is not null) OutputReceived += outputReceived;
        if (tcpProbe is not null)
        {
            _probe = new(tcpProbe, new TcpProbeConnector(), TimeProvider.System);
            _probe.Changed += (_, snapshot) =>
            {
                try { TcpProbeChanged?.Invoke(this, snapshot); }
                catch { /* 订阅者不能影响进程生命周期。 */ }
            };
        }
        _completion = ObserveAsync(stdout, stderr);
    }

    public event EventHandler<CommandOutput>? OutputReceived;
    public event EventHandler<TcpProbeSnapshot>? TcpProbeChanged;
    public TcpProbeSnapshot TcpProbe => _probe?.Snapshot ?? new(TcpProbeState.NotConfigured, 0, null);
    public bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;
    public bool IsStopping => Volatile.Read(ref _stopInProgress) != 0;
    public bool IsOwnershipReleased => Volatile.Read(ref _ownershipReleased) != 0;

    public Task<CommandRunResult> Completion => _completion;

    public IReadOnlyList<CommandOutput> GetRecentOutput() => _output.ToArray();

    public async Task<CommandStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsOwnershipReleased)
            {
                return CommandStopResult.AlreadyExited;
            }

            Interlocked.Exchange(ref _stopRequested, 1);
            Interlocked.Exchange(ref _stopInProgress, 1);
            _probe?.Stop();
            if (!_stopOperations.TrySendCtrlC(_processId))
            {
                if (IsOwnershipReleased)
                {
                    return CommandStopResult.AlreadyExited;
                }
            }

            var gracefulExit = await _stopOperations.WaitForCommandGroupExitAsync(_job, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (gracefulExit)
            {
                await ReleaseFaultedOwnershipAsync().ConfigureAwait(false);
                return CommandStopResult.StoppedAfterCtrlC;
            }

            _stopOperations.TerminateJob(_job);
            Interlocked.Exchange(ref _forceTerminated, 1);
            var forcedExit = await _stopOperations.WaitForCommandGroupExitAsync(_job, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!forcedExit)
            {
                throw new TimeoutException("TalosDesk could not confirm that every process in this command group exited after the force-stop request.");
            }

            await ReleaseFaultedOwnershipAsync().ConfigureAwait(false);
            return CommandStopResult.ForceTerminated;
        }
        finally
        {
            Interlocked.Exchange(ref _stopInProgress, 0);
            _stopLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsOwnershipReleased)
        {
            await StopAsync().ConfigureAwait(false);
        }

        try { await _completion.ConfigureAwait(false); }
        finally
        {
            if (IsOwnershipReleased)
            {
                _process.Dispose();
                _job.Dispose();
            }
        }
    }

    private async Task ReleaseFaultedOwnershipAsync()
    {
        if (!_completion.IsCompleted) return; // 正常观察仍需读取退出码和排空输出。
        try { await _outputDrain.ConfigureAwait(false); }
        catch { /* 输出观察失败不撤销已经确认的归属退出。 */ }
        Interlocked.Exchange(ref _ownershipReleased, 1);
        _process.Dispose();
        _job.Dispose();
    }

    private async Task<CommandRunResult> ObserveAsync(StreamReader stdout, StreamReader stderr)
    {
        try
        {
            var stdoutTask = PumpAsync(stdout, "stdout", _sensitiveValues, Emit);
            var stderrTask = PumpAsync(stderr, "stderr", _sensitiveValues, Emit);
            _outputDrain = Task.WhenAll(stdoutTask, stderrTask);
            await _observation.WaitForProcessAsync(_process).ConfigureAwait(false);
            await _observation.WaitForGroupAsync(_job).ConfigureAwait(false);
            _probe?.Stop();
            await _outputDrain.ConfigureAwait(false);

            var exitCode = _observation.GetExitCode(_process);
            var stopped = Volatile.Read(ref _stopRequested) != 0;
            var forced = Volatile.Read(ref _forceTerminated) != 0;
            var state = stopped ? CommandRunState.Stopped : exitCode == 0 ? CommandRunState.Succeeded : CommandRunState.Failed;
            Interlocked.Exchange(ref _ownershipReleased, 1);
            return new CommandRunResult(state, exitCode, forced);
        }
        finally
        {
            if (_probe is not null) await _probe.DisposeAsync().ConfigureAwait(false);
            await _stopLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 观察失败时保留控制资源，允许重试停止；不能把 faulted Completion 当作退出。
                if (IsOwnershipReleased)
                {
                    _process.Dispose();
                    _job.Dispose();
                }
            }
            finally
            {
                _stopLock.Release();
            }
        }
    }

    internal static async Task PumpAsync(StreamReader reader, string stream, IReadOnlyList<string> sensitiveValues,
        Action<string, string> emit, TimeSpan? redactionTimeout = null)
    {
        using (reader)
        {
            var redactor = new StreamingOutputRedactor(sensitiveValues, redactionTimeout);
            var line = new StringBuilder();
            var hasLineSegments = false;
            var buffer = new char[16_384];
            try
            {
                int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
                {
                    var text = redactor.Append(new string(buffer, 0, length));
                    if (redactor.HasFailed && text.Length > 0) PublishOmission(line, stream, emit);
                    else AppendSanitized(text, line, stream, emit, ref hasLineSegments);
                }
                var remainder = redactor.Finish();
                if (redactor.HasFailed && remainder.Length > 0) PublishOmission(line, stream, emit);
                else AppendSanitized(remainder, line, stream, emit, ref hasLineSegments);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!redactor.HasFailed) PublishOmission(line, stream, emit);
            }
            if (line.Length > 0) emit(line.ToString(), stream);
        }
    }

    private static void PublishOmission(StringBuilder line, string stream, Action<string, string> emit)
    {
        // 长驻命令不能等EOF才显示失败；独立发布提示，避免被16 KiB半行拆开。
        if (line.Length > 0)
        {
            emit(line.ToString(), stream);
            line.Clear();
        }
        emit(StreamingOutputRedactor.OmittedMarker, stream);
    }

    private static void AppendSanitized(string text, StringBuilder line, string stream, Action<string, string> emit,
        ref bool hasLineSegments)
    {
        foreach (var character in text)
        {
            if (character == '\n')
            {
                var count = line.Length > 0 && line[^1] == '\r' ? line.Length - 1 : line.Length;
                if (count > 0 || !hasLineSegments) emit(line.ToString(0, count), stream);
                line.Clear();
                hasLineSegments = false;
                continue;
            }
            line.Append(character);
            if (line.Length < 16_384) continue;
            // 保留边界上的高代理项和回车，避免拆开补充字符或跨块CRLF。
            var segmentLength = line.Length - (char.IsHighSurrogate(line[^1]) || line[^1] == '\r' ? 1 : 0);
            emit(line.ToString(0, segmentLength), stream);
            line.Remove(0, segmentLength);
            hasLineSegments = true;
        }
    }

    private void Emit(string text, string stream)
    {
        var entry = new CommandOutput(DateTimeOffset.Now, stream, text);
        _output.Enqueue(entry);
        if (Interlocked.Increment(ref _outputCount) > 10_000 && _output.TryDequeue(out _))
            Interlocked.Decrement(ref _outputCount);
        try { OutputReceived?.Invoke(this, entry); }
        catch { /* A view subscriber must not stop output draining. */ }
    }
}
