using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class CommandGroupExecutionTests
{
    [TestMethod]
    public void ParallelPlanSkipsBusyCommandsAndKeepsGroupOrder()
    {
        var first = NewCommand("First");
        var second = NewCommand("Second");
        var third = NewCommand("Third");
        var project = new ProjectDefinition { Commands = [first, second, third] };
        var group = new CommandGroupDefinition { Name = "Parallel", CommandIds = [third.Id, first.Id, second.Id] };

        var ready = CommandGroupExecution.GetParallelReadyCommands(project, group, id => id == first.Id);

        CollectionAssert.AreEqual(new[] { third.Id, second.Id }, ready.Select(command => command.Id).ToArray());
    }

    [TestMethod]
    public async Task ParallelObservationReportsTaskFailureWithoutWaitingForService()
    {
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;
        var test = NewCommand("Test");
        var serviceCompletion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = CommandGroupExecution.ObserveParallelAsync(
        [
            new ParallelCommandExecution(service, serviceCompletion.Task),
            new ParallelCommandExecution(test, Task.FromResult(new CommandRunResult(CommandRunState.Failed, 7, false)))
        ]);

        var result = await observation.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(ParallelGroupObservationOutcome.CommandFailed, result.Outcome);
        Assert.AreSame(test, result.Command);
        Assert.AreEqual(7, result.RunResult?.ExitCode);
        Assert.IsFalse(serviceCompletion.Task.IsCompleted);
    }

    [TestMethod]
    public async Task ParallelObservationCompletesWhenTasksSucceedAndReportsStoppedTask()
    {
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;
        var test = NewCommand("Test");
        var serviceCompletion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var succeeded = await CommandGroupExecution.ObserveParallelAsync(
        [
            new ParallelCommandExecution(service, serviceCompletion.Task),
            new ParallelCommandExecution(test, Task.FromResult(new CommandRunResult(CommandRunState.Succeeded, 0, false)))
        ]).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(ParallelGroupObservationOutcome.TasksSucceeded, succeeded.Outcome);

        var stopped = await CommandGroupExecution.ObserveParallelAsync(
        [new ParallelCommandExecution(test, Task.FromResult(new CommandRunResult(CommandRunState.Stopped, 1, false)))]);
        Assert.AreEqual(ParallelGroupObservationOutcome.TaskStopped, stopped.Outcome);
    }

    [TestMethod]
    public async Task SequentialExecutionRunsInOrderAndStopsAfterFailure()
    {
        var first = NewCommand("First");
        var second = NewCommand("Second");
        var third = NewCommand("Third");
        var started = new List<Guid>();

        var result = await CommandGroupExecution.RunSequentialAsync([first, second, third], (command, _) =>
        {
            started.Add(command.Id);
            return Task.FromResult(new CommandRunResult(
                command.Id == second.Id ? CommandRunState.Failed : CommandRunState.Succeeded,
                command.Id == second.Id ? 1 : 0,
                false));
        });

        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, started);
        Assert.AreEqual(SequentialGroupStopReason.CommandFailed, result.StopReason);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreSame(second, result.LastCommand);
    }

    [TestMethod]
    public async Task SequentialExecutionStopsAfterStoppedCommandOrStartFailure()
    {
        var first = NewCommand("First");
        var second = NewCommand("Second");
        var stopped = await CommandGroupExecution.RunSequentialAsync([first, second], (command, _) =>
            Task.FromResult(new CommandRunResult(CommandRunState.Stopped, 1, false)));
        Assert.AreEqual(SequentialGroupStopReason.CommandStopped, stopped.StopReason);
        Assert.AreEqual(1, stopped.CompletedCount);

        var failedStart = await CommandGroupExecution.RunSequentialAsync([first, second], (_, _) =>
            throw new IOException("synthetic start failure"));
        Assert.AreEqual(SequentialGroupStopReason.StartFailed, failedStart.StopReason);
        Assert.AreEqual(0, failedStart.CompletedCount);
        Assert.IsInstanceOfType<IOException>(failedStart.StartException);
    }

    [TestMethod]
    public async Task SequentialExecutionHonorsCancellationBeforeStartingNextCommand()
    {
        var cancellation = new CancellationTokenSource();
        var first = NewCommand("First");
        var second = NewCommand("Second");
        var started = new List<Guid>();

        var result = await CommandGroupExecution.RunSequentialAsync([first, second], (command, _) =>
        {
            started.Add(command.Id);
            cancellation.Cancel();
            return Task.FromResult(new CommandRunResult(CommandRunState.Succeeded, 0, false));
        }, cancellation.Token);

        CollectionAssert.AreEqual(new[] { first.Id }, started);
        Assert.AreEqual(SequentialGroupStopReason.Cancelled, result.StopReason);
    }

    [TestMethod]
    public async Task SequentialExecutionRejectsServices()
    {
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => CommandGroupExecution.RunSequentialAsync([service], (_, _) =>
            Task.FromResult(new CommandRunResult(CommandRunState.Succeeded, 0, false))));
    }

    private static CommandDefinition NewCommand(string name) => new() { Name = name, Command = "exit 0", WorkingDirectory = Environment.CurrentDirectory };
}
