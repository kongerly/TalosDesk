namespace TalosDesk.Core.Processes;

public sealed class CommandRunner
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CommandRunSession> _sessions = [];

    public CommandRunSession Start(Guid commandId, string command, string workingDirectory, EventHandler<CommandOutput>? outputReceived = null)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command is required.", nameof(command));

        lock (_sync)
        {
            if (_sessions.TryGetValue(commandId, out var current) && !current.Completion.IsCompleted)
            {
                throw new InvalidOperationException("This command is already running.");
            }

            var started = WindowsNative.StartPowerShell(command, Path.GetFullPath(workingDirectory));
            var session = new CommandRunSession(started.Job, started.Process, started.ProcessId, started.Stdout, started.Stderr, outputReceived);
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
