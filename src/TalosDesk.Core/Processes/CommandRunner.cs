using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Processes;

public sealed class CommandRunner
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CommandRunSession> _sessions = [];
    private readonly ICommandStopOperations _stopOperations;

    public CommandRunner() : this(WindowsCommandStopOperations.Instance) { }

    internal CommandRunner(ICommandStopOperations stopOperations) =>
        _stopOperations = stopOperations ?? throw new ArgumentNullException(nameof(stopOperations));

    public CommandRunSession Start(Guid commandId, string command, string workingDirectory,
        EventHandler<CommandOutput>? outputReceived = null, CommandRunEnvironment? runEnvironment = null,
        TcpProbeConfiguration? tcpProbe = null)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command is required.", nameof(command));
        var probeSnapshot = tcpProbe?.Clone();
        probeSnapshot?.Validate();
        if (runEnvironment?.SensitiveValues is { } values &&
            values.Any(value => value is null || value.Length > 4096))
            throw new ArgumentException("The command has an invalid sensitive value for output redaction.", nameof(runEnvironment));

        lock (_sync)
        {
            if (_sessions.TryGetValue(commandId, out var current) && !current.Completion.IsCompleted)
            {
                throw new InvalidOperationException("This command is already running.");
            }

            var started = WindowsNative.StartPowerShell(command, Path.GetFullPath(workingDirectory), runEnvironment?.Overrides);
            var session = new CommandRunSession(started.Job, started.Process, started.ProcessId, started.Stdout, started.Stderr,
                outputReceived, _stopOperations, runEnvironment?.SensitiveValues ?? [], probeSnapshot);
            _sessions[commandId] = session;
            _ = session.Completion.ContinueWith(_ => RemoveCompleted(commandId, session), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return session;
        }
    }

    public bool IsRunning(Guid commandId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(commandId, out var session) && !session.Completion.IsCompleted;
        }
    }

    private void RemoveCompleted(Guid commandId, CommandRunSession session)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(commandId, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(commandId);
            }
        }
    }
}
