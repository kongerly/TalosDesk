using System.Collections.Concurrent;

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
    private readonly ConcurrentQueue<CommandOutput> _output = new();
    private readonly Task<CommandRunResult> _completion;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _outputCount;
    private int _stopRequested;
    private int _forceTerminated;

    internal CommandRunSession(SafeJobHandle job, SafeProcessHandle process, int processId, StreamReader stdout, StreamReader stderr, EventHandler<CommandOutput>? outputReceived, ICommandStopOperations stopOperations)
    {
        _job = job;
        _process = process;
        _processId = processId;
        _stopOperations = stopOperations;
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

    private Task PumpAsync(StreamReader reader, string stream) => Task.Run(() =>
    {
        using (reader)
        {
            while (reader.ReadLine() is { } line)
            {
                var entry = new CommandOutput(DateTimeOffset.Now, stream, line);
                _output.Enqueue(entry);
                if (Interlocked.Increment(ref _outputCount) > 10_000 && _output.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _outputCount);
                }

                try { OutputReceived?.Invoke(this, entry); }
                catch { /* A view subscriber must not stop output draining. */ }
            }
        }
    });
}
