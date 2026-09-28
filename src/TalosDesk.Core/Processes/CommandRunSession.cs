using System.Collections.Concurrent;
using System.Text;

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

public sealed class CommandRunSession : IAsyncDisposable
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

    internal CommandRunSession(SafeJobHandle job, SafeProcessHandle process, int processId, StreamReader stdout, StreamReader stderr,
        EventHandler<CommandOutput>? outputReceived, ICommandStopOperations stopOperations, IReadOnlyList<string> sensitiveValues)
    {
        _job = job;
        _process = process;
        _processId = processId;
        _stopOperations = stopOperations;
        _sensitiveValues = sensitiveValues;
        if (outputReceived is not null) OutputReceived += outputReceived;
        _completion = ObserveAsync(stdout, stderr);
    }

    public event EventHandler<CommandOutput>? OutputReceived;

    public Task<CommandRunResult> Completion => _completion;

    public IReadOnlyList<CommandOutput> GetRecentOutput() => _output.ToArray();

    public async Task<CommandStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_completion.IsCompleted)
            {
                return CommandStopResult.AlreadyExited;
            }

            Interlocked.Exchange(ref _stopRequested, 1);
            if (!_stopOperations.TrySendCtrlC(_processId))
            {
                if (_completion.IsCompleted)
                {
                    return CommandStopResult.AlreadyExited;
                }
            }

            var gracefulExit = await _stopOperations.WaitForCommandGroupExitAsync(_job, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (gracefulExit)
            {
                return CommandStopResult.StoppedAfterCtrlC;
            }

            _stopOperations.TerminateJob(_job);
            Interlocked.Exchange(ref _forceTerminated, 1);
            var forcedExit = await _stopOperations.WaitForCommandGroupExitAsync(_job, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!forcedExit)
            {
                throw new TimeoutException("TalosDesk could not confirm that every process in this command group exited after the force-stop request.");
            }

            return CommandStopResult.ForceTerminated;
        }
        finally
        {
            _stopLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completion.IsCompleted)
        {
            await StopAsync().ConfigureAwait(false);
        }

        await _completion.ConfigureAwait(false);
        _process.Dispose();
        _job.Dispose();
    }

    private async Task<CommandRunResult> ObserveAsync(StreamReader stdout, StreamReader stderr)
    {
        try
        {
            var stdoutTask = PumpAsync(stdout, "stdout");
            var stderrTask = PumpAsync(stderr, "stderr");
            await WindowsNative.WaitForProcessAsync(_process, Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            await WindowsNative.WaitForCommandGroupExitAsync(_job, Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var exitCode = WindowsNative.GetExitCode(_process);
            var stopped = Volatile.Read(ref _stopRequested) != 0;
            var forced = Volatile.Read(ref _forceTerminated) != 0;
            var state = stopped ? CommandRunState.Stopped : exitCode == 0 ? CommandRunState.Succeeded : CommandRunState.Failed;
            return new CommandRunResult(state, exitCode, forced);
        }
        finally
        {
            await _stopLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _process.Dispose();
                _job.Dispose();
            }
            finally
            {
                _stopLock.Release();
            }
        }
    }

    private async Task PumpAsync(StreamReader reader, string stream)
    {
        using (reader)
        {
            var redactor = new StreamingOutputRedactor(_sensitiveValues);
            var line = new StringBuilder();
            var buffer = new char[16_384];
            try
            {
                int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
                {
                    AppendSanitized(redactor.Append(new string(buffer, 0, length)), line, stream);
                }
                AppendSanitized(redactor.Finish(), line, stream);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppendSanitized(StreamingOutputRedactor.OmittedMarker, line, stream);
            }
            if (line.Length > 0) Emit(line.ToString(), stream);
        }
    }

    private void AppendSanitized(string text, StringBuilder line, string stream)
    {
        foreach (var character in text)
        {
            if (character == '\n')
            {
                var count = line.Length > 0 && line[^1] == '\r' ? line.Length - 1 : line.Length;
                Emit(line.ToString(0, count), stream);
                line.Clear();
                continue;
            }
            line.Append(character);
            if (line.Length < 16_384) continue;
            Emit(line.ToString(), stream);
            line.Clear();
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
