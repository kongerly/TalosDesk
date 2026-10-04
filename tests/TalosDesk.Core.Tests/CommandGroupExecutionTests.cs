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
    public async Task ParallelObservationReportsAlreadyFailedServiceWithoutWaitingForOtherServices()
    {
        var failedService = NewCommand("Failed server");
        failedService.Kind = CommandKind.Service;
        var otherService = NewCommand("Other server");
        otherService.Kind = CommandKind.Service;
        var pending = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await CommandGroupExecution.ObserveParallelAsync(
        [
            new ParallelCommandExecution(otherService, pending.Task),
            new ParallelCommandExecution(failedService, Task.FromResult(new CommandRunResult(CommandRunState.Failed, 9, false)))
        ]).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(ParallelGroupObservationOutcome.CommandFailed, result.Outcome);
        Assert.AreSame(failedService, result.Command);
        Assert.AreEqual(9, result.RunResult?.ExitCode);
        Assert.IsFalse(pending.Task.IsCompleted);
    }

    [TestMethod]
    public async Task ParallelObservationKeepsWatchingServiceOnlyGroupUntilFailure()
    {
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;
        var completion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = CommandGroupExecution.ObserveParallelAsync([new ParallelCommandExecution(service, completion.Task)]);

        Assert.IsFalse(observation.IsCompleted);
        completion.SetResult(new(CommandRunState.Failed, 7, false));
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(ParallelGroupObservationOutcome.CommandFailed, result.Outcome);
        Assert.AreSame(service, result.Command);
        Assert.AreEqual(7, result.RunResult?.ExitCode);
    }

    [TestMethod]
    public async Task ParallelObservationKeepsWatchingServiceAfterTasksSucceed()
    {
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;
        var test = NewCommand("Test");
        var serviceCompletion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var observation = CommandGroupExecution.ObserveParallelAsync(
        [
            new ParallelCommandExecution(service, serviceCompletion.Task),
            new ParallelCommandExecution(test, Task.FromResult(new CommandRunResult(CommandRunState.Succeeded, 0, false)))
        ]);

        Assert.IsFalse(observation.IsCompleted);
        serviceCompletion.SetResult(new(CommandRunState.Failed, 11, false));
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(ParallelGroupObservationOutcome.CommandFailed, result.Outcome);
        Assert.AreSame(service, result.Command);
        Assert.AreEqual(11, result.RunResult?.ExitCode);
    }

    [TestMethod]
    public async Task ParallelObservationReportsStoppedTask()
    {
        var test = NewCommand("Test");
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;
        var serviceCompletion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = await CommandGroupExecution.ObserveParallelAsync(
        [
            new ParallelCommandExecution(service, serviceCompletion.Task),
            new ParallelCommandExecution(test, Task.FromResult(new CommandRunResult(CommandRunState.Stopped, 1, false)))
        ]).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(ParallelGroupObservationOutcome.TaskStopped, stopped.Outcome);
        Assert.IsFalse(serviceCompletion.Task.IsCompleted);
    }

    [TestMethod]
    [DataRow(CommandRunState.Succeeded)]
    [DataRow(CommandRunState.Stopped)]
    public async Task ParallelObservationNotifiesTaskSuccessOnceAndWaitsForServiceExit(CommandRunState serviceState)
    {
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;
        var firstTask = NewCommand("First task");
        var lastTask = NewCommand("Last task");
        var serviceCompletion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskCompletion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        var observation = CommandGroupExecution.ObserveParallelAsync(
        [
            new ParallelCommandExecution(service, serviceCompletion.Task),
            new ParallelCommandExecution(firstTask, Task.FromResult(new CommandRunResult(CommandRunState.Succeeded, 0, false))),
            new ParallelCommandExecution(lastTask, taskCompletion.Task)
        ], () =>
        {
            notificationCount++;
            notification.SetResult();
            return Task.CompletedTask;
        });

        Assert.AreEqual(0, notificationCount);
        taskCompletion.SetResult(new(CommandRunState.Succeeded, 0, false));
        await notification.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(observation.IsCompleted);
        serviceCompletion.SetResult(new(serviceState, 0, false));
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, notificationCount);
        Assert.AreEqual(ParallelGroupObservationOutcome.TasksSucceeded, result.Outcome);
        Assert.AreSame(lastTask, result.Command);
    }

    [TestMethod]
    [DataRow(CommandRunState.Succeeded)]
    [DataRow(CommandRunState.Stopped)]
    public async Task ParallelObservationWaitsForEveryServiceEvenAfterNormalExit(CommandRunState firstState)
    {
        var first = NewCommand("First server");
        first.Kind = CommandKind.Service;
        var second = NewCommand("Second server");
        second.Kind = CommandKind.Service;
        var completion = new TaskCompletionSource<CommandRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = CommandGroupExecution.ObserveParallelAsync(
        [
            new ParallelCommandExecution(first, Task.FromResult(new CommandRunResult(firstState, 0, false))),
            new ParallelCommandExecution(second, completion.Task)
        ], () => throw new AssertFailedException("纯服务分组不应发送任务完成通知。"));

        Assert.IsFalse(observation.IsCompleted);
        completion.SetResult(new(CommandRunState.Failed, 5, false));
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(ParallelGroupObservationOutcome.CommandFailed, result.Outcome);
        Assert.AreSame(second, result.Command);
    }

    [TestMethod]
    public async Task ParallelObservationCompletesEmptyServiceOnlyAndTaskOnlyGroups()
    {
        var service = NewCommand("Server");
        service.Kind = CommandKind.Service;
        var task = NewCommand("Test");
        var empty = await CommandGroupExecution.ObserveParallelAsync([]);
        var serviceOnly = await CommandGroupExecution.ObserveParallelAsync(
            [new ParallelCommandExecution(service, Task.FromResult(new CommandRunResult(CommandRunState.Stopped, 0, false)))]);
        var notifications = 0;
        var taskOnly = await CommandGroupExecution.ObserveParallelAsync(
            [new ParallelCommandExecution(task, Task.FromResult(new CommandRunResult(CommandRunState.Succeeded, 0, false)))], () =>
            {
                notifications++;
                return Task.CompletedTask;
            });

        Assert.AreEqual(ParallelGroupObservationOutcome.NoTaskCommands, empty.Outcome);
        Assert.AreEqual(ParallelGroupObservationOutcome.NoTaskCommands, serviceOnly.Outcome);
        Assert.AreEqual(ParallelGroupObservationOutcome.TasksSucceeded, taskOnly.Outcome);
        Assert.AreEqual(1, notifications);
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
