using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Processes;

public enum SequentialGroupStopReason
{
    Completed,
    StartFailed,
    CommandFailed,
    CommandStopped,
    Cancelled
}

public sealed record SequentialGroupExecutionResult(
    SequentialGroupStopReason StopReason,
    int CompletedCount,
    CommandDefinition? LastCommand,
    Exception? StartException = null);

public enum ParallelGroupObservationOutcome
{
    NoTaskCommands,
    TasksSucceeded,
    CommandFailed,
    TaskStopped
}

public sealed record ParallelCommandExecution(CommandDefinition Command, Task<CommandRunResult> Completion);

public sealed record ParallelGroupObservationResult(
    ParallelGroupObservationOutcome Outcome,
    CommandDefinition? Command = null,
    CommandRunResult? RunResult = null);

public static class CommandGroupExecution
{
    public static IReadOnlyList<CommandDefinition> ResolveCommands(ProjectDefinition project, CommandGroupDefinition group)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(group);
        var commands = project.Commands.ToDictionary(command => command.Id);
        return group.CommandIds.Select(commandId => commands.TryGetValue(commandId, out var command)
            ? command
            : throw new InvalidDataException($"Command group '{group.Name}' references a missing command.")).ToArray();
    }

    public static IReadOnlyList<CommandDefinition> GetParallelReadyCommands(
        ProjectDefinition project,
        CommandGroupDefinition group,
        Func<Guid, bool> isBusy)
    {
        ArgumentNullException.ThrowIfNull(isBusy);
        return ResolveCommands(project, group).Where(command => !isBusy(command.Id)).ToArray();
    }

    public static async Task<ParallelGroupObservationResult> ObserveParallelAsync(
        IReadOnlyList<ParallelCommandExecution> executions,
        Func<Task>? onTasksSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(executions);
        var remainingTasks = executions.Count(execution => execution.Command.Kind == CommandKind.Task);
        ParallelGroupObservationResult? tasksSucceeded = null;

        var pending = executions.ToDictionary(execution => execution.Completion);
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Keys).ConfigureAwait(false);
            var execution = pending[completed];
            pending.Remove(completed);
            var result = await completed.ConfigureAwait(false);
            if (result.State == CommandRunState.Failed)
            {
                return new ParallelGroupObservationResult(ParallelGroupObservationOutcome.CommandFailed, execution.Command, result);
            }

            if (execution.Command.Kind != CommandKind.Task) continue;
            if (result.State == CommandRunState.Stopped)
            {
                return new ParallelGroupObservationResult(ParallelGroupObservationOutcome.TaskStopped, execution.Command, result);
            }

            remainingTasks--;
            if (remainingTasks == 0)
            {
                tasksSucceeded = new ParallelGroupObservationResult(ParallelGroupObservationOutcome.TasksSucceeded, execution.Command, result);
                // 任务完成只发送进度通知；服务仍属于本次执行，继续监视其退出结果。
                if (onTasksSucceeded is not null) await onTasksSucceeded().ConfigureAwait(false);
            }
        }

        return tasksSucceeded ?? new ParallelGroupObservationResult(ParallelGroupObservationOutcome.NoTaskCommands);
    }

    public static async Task<SequentialGroupExecutionResult> RunSequentialAsync(
        IReadOnlyList<CommandDefinition> commands,
        Func<CommandDefinition, CancellationToken, Task<CommandRunResult>> runCommand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(runCommand);
        if (commands.Any(command => command.Kind == CommandKind.Service))
        {
            throw new ArgumentException("Sequential command groups can contain task commands only.", nameof(commands));
        }

        var completedCount = 0;
        CommandDefinition? lastCommand = null;
        foreach (var command in commands)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SequentialGroupExecutionResult(SequentialGroupStopReason.Cancelled, completedCount, lastCommand);
            }

            lastCommand = command;
            CommandRunResult result;
            try
            {
                result = await runCommand(command, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SequentialGroupExecutionResult(SequentialGroupStopReason.Cancelled, completedCount, command);
            }
            catch (Exception exception)
            {
                return new SequentialGroupExecutionResult(SequentialGroupStopReason.StartFailed, completedCount, command, exception);
            }

            completedCount++;
            if (result.State == CommandRunState.Succeeded) continue;
            return new SequentialGroupExecutionResult(
                result.State == CommandRunState.Stopped ? SequentialGroupStopReason.CommandStopped : SequentialGroupStopReason.CommandFailed,
                completedCount,
                command);
        }

        return new SequentialGroupExecutionResult(SequentialGroupStopReason.Completed, completedCount, lastCommand);
    }
}
