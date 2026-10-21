using System.Collections.Concurrent;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class CommandRecoveryCoordinatorTests
{
    [TestMethod]
    public async Task UsesExactlyThreeAttemptsWithMonotonicTwoFourEightSecondDelaysAndNeverResetsWhileRunning()
    {
        var h = new Harness();
        var first = await h.Start();
        var round = h.Snapshot.RoundId;
        long version = h.Snapshot.SessionVersion;
        Assert.IsNotNull(round);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            h.Latest.Finish(CommandRunState.Failed, 7);
            await h.State(CommandRecoveryState.Waiting);
            Assert.AreEqual(attempt, h.Snapshot.Attempts);
            Assert.AreEqual(TimeSpan.FromSeconds(2 << attempt), h.Snapshot.RemainingDelay);
            Assert.IsTrue(h.Coordinator.IsBusy(h.Id));
            h.Clock.JumpWallClock();
            h.Clock.Advance((2 << attempt) - 1);
            Assert.HasCount(attempt + 1, h.Sessions);
            h.Clock.Advance(1);
            await h.Count(attempt + 2);
            Assert.AreEqual(attempt + 1, h.Snapshot.Attempts);
            Assert.AreEqual(round, h.Snapshot.RoundId);
            Assert.IsGreaterThan(version, h.Snapshot.SessionVersion);
            version = h.Snapshot.SessionVersion;
            h.Clock.Advance(100_000); // 运行再久也不返还次数，不补发错过的尝试。
            Assert.HasCount(attempt + 2, h.Sessions);
        }
        h.Latest.Finish(CommandRunState.Failed, 9);
        await h.State(CommandRecoveryState.Exhausted);
        h.Clock.Advance(100_000);
        Assert.HasCount(4, h.Sessions);
        Assert.AreEqual(3, h.Snapshot.Attempts);
        Assert.IsFalse(h.Snapshot.IsBusy);
        Assert.AreEqual(7, (await first.Completion).ExitCode);
        Assert.AreEqual(0, h.Clock.Pending);
    }

    [TestMethod]
    [DataRow(false, CommandKind.Service, CommandStartSource.Independent, CommandRunState.Failed, 7)]
    [DataRow(true, CommandKind.Task, CommandStartSource.Independent, CommandRunState.Failed, 7)]
    [DataRow(true, CommandKind.Service, CommandStartSource.Group, CommandRunState.Failed, 7)]
    [DataRow(true, CommandKind.Service, CommandStartSource.Independent, CommandRunState.Succeeded, 0)]
    [DataRow(true, CommandKind.Service, CommandStartSource.Independent, CommandRunState.Failed, 0)]
    [DataRow(true, CommandKind.Service, CommandStartSource.Independent, CommandRunState.Stopped, 7)]
    public async Task OnlyIndependentEnabledServiceNonzeroFailuresRecover(bool enabled, CommandKind kind, CommandStartSource source, CommandRunState state, int code)
    {
        var h = new Harness();
        await h.Coordinator.StartAsync(h.Request with { AutoRestartEnabled = enabled, Kind = kind, Source = source });
        h.Latest.Finish(state, code);
        await h.State(CommandRecoveryState.Completed);
        h.Clock.Advance(100);
        Assert.HasCount(1, h.Sessions);
        Assert.AreEqual(0, h.Snapshot.Attempts);
        if (!enabled || kind == CommandKind.Task || source == CommandStartSource.Group) Assert.IsNull(h.Snapshot.RoundId);
    }

    [TestMethod]
    public async Task DefaultRequestDoesNotEnableRecoveryAndNormalAutoExitEndsRound()
    {
        var h = new Harness();
        Assert.IsFalse(new CommandStartRequest(h.Id, CommandKind.Service, CommandStartSource.Independent, h.Prepare).AutoRestartEnabled);
        await h.Start();
        await h.FailAndWait();
        h.Clock.Advance(2);
        await h.Count(2);
        h.Latest.Finish(CommandRunState.Succeeded, 0);
        await h.State(CommandRecoveryState.Completed);
        Assert.AreEqual(1, h.Snapshot.Attempts);
        Assert.AreEqual(0, h.Clock.Pending);
    }

    [TestMethod]
    public async Task IndependentBatchBudgetsAreSeparateAndGroupSkipsReferencedWaitingService()
    {
        var h = new Harness();
        var service = new CommandDefinition { Id = h.Id, Kind = CommandKind.Service };
        var other = new CommandDefinition { Kind = CommandKind.Service };
        var group = new CommandGroupDefinition { CommandIds = [service.Id, other.Id] };
        var project = new ProjectDefinition { Commands = [service, other], Groups = [group] };
        await h.Start();
        var second = (FakeSession)await h.Coordinator.StartAsync(h.Request with { CommandId = other.Id });
        await h.FailAndWait(h.Sessions[0]);
        var ready = CommandGroupExecution.GetParallelReadyCommands(project, group, h.Coordinator.IsBusy);
        Assert.HasCount(0, ready);
        h.Clock.Advance(2);
        await h.Count(3);
        Assert.AreEqual(1, h.Snapshot.Attempts);
        Assert.AreEqual(0, h.Coordinator.GetSnapshot(other.Id)!.Attempts);
        second.Finish(CommandRunState.Succeeded, 0);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialPreflightOrCreationFailureDoesNotEstablishRound(bool creation)
    {
        var h = new Harness();
        if (creation) h.CreateFailure = new IOException("synthetic");
        else h.PreflightFailure = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => h.Start());
        Assert.IsNull(h.Snapshot.RoundId);
        Assert.AreEqual(0, h.Snapshot.Attempts);
        Assert.IsFalse(h.Snapshot.IsBusy);
        Assert.AreEqual(creation ? CommandRecoveryError.CreationFailed : CommandRecoveryError.PreflightFailed, h.Snapshot.Error);
        h.Clock.Advance(100);
        Assert.HasCount(0, h.Sessions);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticPreflightOrCreationFailureAbortsWithoutSpendingRemainingBudget(bool creation)
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        if (creation) h.CreateFailure = new IOException("synthetic");
        else h.PreflightFailure = true;
        h.Clock.Advance(2);
        await h.State(CommandRecoveryState.Aborted);
        Assert.AreEqual(creation ? 1 : 0, h.Snapshot.Attempts);
        Assert.AreEqual(creation ? CommandRecoveryError.CreationFailed : CommandRecoveryError.PreflightFailed, h.Snapshot.Error);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        Assert.IsFalse(h.Snapshot.IsBusy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObservationOrCreationRollbackFaultRetainsOccupancyForEverySourceUntilExplicitStopConfirmsExit(bool creation)
    {
        var h = new Harness();
        await h.Start();
        FakeSession unknown;
        if (creation)
        {
            await h.FailAndWait();
            unknown = new FakeSession();
            unknown.Fault();
            h.CreateFailure = new CommandCreationException(unknown, new IOException("synthetic rollback"));
            h.Clock.Advance(2);
        }
        else
        {
            unknown = h.Latest;
            unknown.Fault();
        }
        await h.State(CommandRecoveryState.OwnershipUnconfirmed);
        Assert.IsTrue(h.Snapshot.IsBusy);
        Assert.AreSame(unknown, h.Snapshot.Session);
        foreach (var source in Enum.GetValues<CommandStartSource>())
            Assert.ThrowsExactly<InvalidOperationException>(() => h.Coordinator.StartAsync(h.Request with { Source = source }));
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Coordinator.RestartAsync(h.Request));
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        await h.Coordinator.StopAsync(h.Id);
        Assert.IsTrue(unknown.IsOwnershipReleased);
        Assert.IsFalse(h.Snapshot.IsBusy);
        h.CreateFailure = null;
        var replacement = await h.Start();
        Assert.AreNotSame(unknown, replacement);
        Assert.AreEqual(0, h.Snapshot.Attempts);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task CompletionAndLogFinalizationAreSeparateBarriersAndLogFailureKeepsRealResult()
    {
        var h = new Harness();
        var logging = Signal();
        var release = Signal();
        var session = await h.Coordinator.StartAsync(h.Request with { FinalizeSession = async (_, _) =>
        {
            logging.SetResult();
            await release.Task;
            throw new IOException("synthetic log failure");
        } });
        // 进程/Job/输出未完成期间推进时钟没有效果。
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        h.Latest.Finish(CommandRunState.Failed, 17);
        await logging.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(CommandRunState.Failed, (await session.Completion).State);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        Assert.AreEqual(0, h.Clock.Pending);
        release.SetResult();
        await h.State(CommandRecoveryState.Waiting);
        Assert.IsTrue(h.Snapshot.LogFinalizationFailed);
        Assert.AreEqual(TimeSpan.FromSeconds(2), h.Snapshot.RemainingDelay);
        Assert.AreEqual(17, h.Snapshot.LastResult!.ExitCode);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task StopDuringWaitNeedsNoProcessAndDoesNotRewriteFailedCompletion()
    {
        var h = new Harness();
        var session = await h.Start();
        await h.FailAndWait();
        await h.Coordinator.StopAsync(h.Id);
        Assert.AreEqual(CommandRecoveryState.Cancelled, h.Snapshot.State);
        Assert.IsFalse(h.Snapshot.IsBusy);
        Assert.AreEqual(CommandRunState.Failed, (await session.Completion).State);
        Assert.AreEqual(0, h.Latest.Stops);
        h.Clock.Advance(100);
        Assert.HasCount(1, h.Sessions);
    }

    [TestMethod]
    public async Task StopWhileFailureFinalizationQueuedPreventsAnyRetry()
    {
        var h = new Harness();
        var entered = Signal();
        var release = Signal();
        await h.Coordinator.StartAsync(h.Request with { FinalizeSession = async (_, _) => { entered.SetResult(); await release.Task; } });
        h.Latest.Finish(CommandRunState.Failed, 7);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Coordinator.StopAsync(h.Id);
        release.SetResult();
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        Assert.AreEqual(CommandRecoveryState.Cancelled, h.Snapshot.State);
        Assert.AreEqual(7, h.Snapshot.LastResult!.ExitCode);
    }

    [TestMethod]
    public async Task StopWinsWhileDueRetryIsInPreflight()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using var preflight = new BarrierGate();
        h.PreflightGate = preflight;
        h.Clock.Advance(2);
        await preflight.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Coordinator.StopAsync(h.Id);
        preflight.Release();
        await preflight.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, h.Snapshot.Attempts);
        Assert.HasCount(1, h.Sessions);
    }

    [TestMethod]
    public async Task CreationWinsThenStopTargetsExactlyTheCommittedSession()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using var creating = new BarrierGate();
        h.CreateGate = creating;
        h.Clock.Advance(2);
        await creating.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = Task.Run(() => h.Coordinator.StopAsync(h.Id));
        creating.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(2, h.Sessions);
        Assert.AreEqual(0, h.Sessions[0].Stops);
        Assert.AreEqual(1, h.Sessions[1].Stops);
        Assert.IsTrue(h.Sessions[1].IsOwnershipReleased);
        h.Clock.Advance(1000);
        Assert.HasCount(2, h.Sessions);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedOrCancelledStopKeepsOwnershipAndNeverRestartsOnLaterFailure(bool cancelled)
    {
        var h = new Harness();
        await h.Start();
        h.Latest.StopFailure = cancelled ? new OperationCanceledException() : new IOException("synthetic");
        await Assert.ThrowsAsync<Exception>(() => h.Coordinator.StopAsync(h.Id));
        Assert.IsTrue(h.Snapshot.IsBusy);
        Assert.AreEqual(CommandRecoveryError.StopFailed, h.Snapshot.Error);
        h.Latest.Finish(CommandRunState.Failed, 7);
        await TcpProbeTests.Until(() => h.Snapshot.Session is null);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        Assert.IsFalse(h.Snapshot.IsBusy);
    }

    [TestMethod]
    public async Task StopCanBeRetriedAfterFailure()
    {
        var h = new Harness();
        await h.Start();
        h.Latest.StopFailure = new IOException("synthetic");
        await Assert.ThrowsExactlyAsync<IOException>(() => h.Coordinator.StopAsync(h.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Start());
        h.Latest.StopFailure = null;
        await h.Coordinator.StopAsync(h.Id);
        Assert.AreEqual(2, h.Latest.Stops);
        await TcpProbeTests.Until(() => !h.Snapshot.IsBusy);
    }

    [TestMethod]
    public async Task WaitingRunAndGroupRequestsCannotStealBudgetAndDuplicateRestartsMerge()
    {
        var h = new Harness();
        await h.Start();
        var round = h.Snapshot.RoundId;
        await h.FailAndWait();
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Start());
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Coordinator.StartAsync(h.Request with { Source = CommandStartSource.Group }));
        using var preflight = new BarrierGate();
        h.PreflightGate = preflight;
        var restart = h.Coordinator.RestartAsync(h.Request);
        await preflight.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(restart, h.Coordinator.RestartAsync(h.Request));
        preflight.Release();
        await restart;
        Assert.AreNotEqual(round, h.Snapshot.RoundId);
        Assert.AreEqual(0, h.Snapshot.Attempts);
        h.Clock.Advance(1000);
        Assert.HasCount(2, h.Sessions);
        await h.Coordinator.StopAsync(h.Id, h.Sessions[0]); // 旧分组/会话清理不能停止新会话。
        Assert.AreEqual(0, h.Latest.Stops);
        Assert.AreSame(h.Latest, h.Snapshot.Session);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task LateOldFinalizerCannotOverwriteNewRoundAndRestartWaitsForOldLogCleanup()
    {
        var h = new Harness();
        var entered = Signal();
        var release = Signal();
        await h.Coordinator.StartAsync(h.Request with { FinalizeSession = async (_, _) => { entered.SetResult(); await release.Task; } });
        var old = h.Latest;
        old.Finish(CommandRunState.Failed, 7);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restart = h.Coordinator.RestartAsync(h.Request);
        Assert.IsFalse(restart.IsCompleted);
        release.SetResult();
        await restart;
        var round = h.Snapshot.RoundId;
        h.Clock.Advance(1000);
        Assert.HasCount(2, h.Sessions);
        Assert.AreEqual(round, h.Snapshot.RoundId);
        Assert.IsNull(h.Snapshot.LastResult);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task RestartPreflightFailureCancelsOldWaitAndPendingRestartIsCancelledByStop()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        h.PreflightFailure = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => h.Coordinator.RestartAsync(h.Request));
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        h.PreflightFailure = false;
        using var preflight = new BarrierGate();
        h.PreflightGate = preflight;
        var restart = h.Coordinator.RestartAsync(h.Request);
        await preflight.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Coordinator.StopAsync(h.Id);
        preflight.Release();
        await Assert.ThrowsAsync<OperationCanceledException>(() => restart);
        Assert.HasCount(1, h.Sessions);
    }

    [TestMethod]
    public async Task PauseDuringCloseConfirmationPreservesDeadlineAndReturningSubmitsOnlyOnce()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using (h.Coordinator.PauseAutomaticStarts())
        {
            h.Clock.Advance(1000);
            Assert.HasCount(1, h.Sessions);
            Assert.AreEqual(0, h.Snapshot.Attempts);
            Assert.AreEqual(TimeSpan.Zero, h.Snapshot.RemainingDelay);
            Assert.IsTrue(h.Snapshot.IsBusy);
        }
        await h.Count(2);
        Assert.AreEqual(1, h.Snapshot.Attempts);
        h.Clock.Advance(1000);
        Assert.HasCount(2, h.Sessions);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfirmedExitCancelsWaitOrQueuedCompletionWithoutAdvancingClock(bool queuedCompletion)
    {
        var h = new Harness();
        var entered = Signal();
        var release = Signal();
        var request = queuedCompletion ? h.Request with { FinalizeSession = async (_, _) => { entered.TrySetResult(); await release.Task; } } : h.Request;
        await h.Coordinator.StartAsync(request);
        h.Latest.Finish(CommandRunState.Failed, 7);
        if (queuedCompletion) await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else await h.State(CommandRecoveryState.Waiting);
        using var pause = h.Coordinator.PauseAutomaticStarts();
        var closing = h.Coordinator.CloseAndStopAsync();
        if (queuedCompletion) Assert.IsFalse(closing.IsCompleted); // 既有日志收尾仍要完成，退避不必等。
        release.TrySetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Start());
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task ConfirmedExitCancelsUncommittedManualRestartEvenIfPreflightIsBlocked()
    {
        var h = new Harness();
        await h.Start();
        using var gate = new BarrierGate();
        h.PreflightGate = gate;
        var restart = h.Coordinator.RestartAsync(h.Request);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Coordinator.CloseAndStopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        await Assert.ThrowsAsync<OperationCanceledException>(() => restart);
        Assert.HasCount(1, h.Sessions);
        Assert.IsTrue(h.Latest.IsOwnershipReleased);
    }

    [TestMethod]
    public async Task ExitAfterCreationCommitStopsExactSessionAndStopFailureDoesNotRestoreRecovery()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using var gate = new BarrierGate();
        h.CreateGate = gate;
        h.Clock.Advance(2);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = Task.Run(() => h.Coordinator.CloseAndStopAsync());
        h.NextStopFailure = new IOException("synthetic");
        gate.Release();
        await Assert.ThrowsExactlyAsync<IOException>(() => closing);
        Assert.HasCount(2, h.Sessions);
        Assert.AreEqual(1, h.Latest.Stops);
        Assert.IsTrue(h.Snapshot.IsBusy);
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Start());
        h.Latest.StopFailure = null;
        await h.Coordinator.CloseAndStopAsync();
        h.Clock.Advance(1000);
        Assert.HasCount(2, h.Sessions);
    }

    [TestMethod]
    public async Task ExternalConfigurationCancellationStopsWaitingButLeavesRunningOwnershipAvailable()
    {
        var h = new Harness();
        await h.Start();
        h.Coordinator.CancelRecovery(h.Id);
        Assert.AreEqual(0, h.Latest.Stops);
        Assert.IsTrue(h.Snapshot.IsBusy);
        h.Latest.Finish(CommandRunState.Failed, 7);
        await TcpProbeTests.Until(() => h.Snapshot.Session is null);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task CancelledStopTokenStillCancelsRecoveryBeforeCallingTheSession()
    {
        var h = new Harness();
        await h.Start();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Coordinator.StopAsync(h.Id, cancellation.Token));
        Assert.IsTrue(h.Snapshot.IsBusy);
        h.Latest.Finish(CommandRunState.Failed, 7);
        await TcpProbeTests.Until(() => h.Snapshot.Session is null);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        Assert.AreEqual(0, h.Snapshot.Attempts);
    }

    [TestMethod]
    public async Task ConfirmedExitReturnsWithoutWaitingForAutomaticPreflightAndCancelsItsCommit()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using var gate = new BarrierGate();
        h.PreflightGate = gate;
        h.Clock.Advance(2);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Coordinator.CloseAndStopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        await gate.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, h.Sessions);
        Assert.AreEqual(0, h.Snapshot.Attempts);
    }

    [TestMethod]
    public async Task LargeSchedulerDelaySubmitsOneAttemptAndStartsNextDelayOnlyAfterNewCompletion()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        h.Clock.Advance(100_000);
        await h.Count(2);
        Assert.AreEqual(1, h.Snapshot.Attempts);
        Assert.AreEqual(0, h.Clock.Pending);
        await h.FailAndWait();
        Assert.AreEqual(TimeSpan.FromSeconds(4), h.Snapshot.RemainingDelay);
        Assert.HasCount(2, h.Sessions);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task LogFailureByItselfCannotTurnSuccessfulSessionIntoRecovery()
    {
        var h = new Harness();
        var session = await h.Coordinator.StartAsync(h.Request with { FinalizeSession = (_, _) => throw new IOException("synthetic log failure") });
        h.Latest.Finish(CommandRunState.Succeeded, 0);
        await h.State(CommandRecoveryState.Completed);
        Assert.IsTrue(h.Snapshot.LogFinalizationFailed);
        Assert.AreEqual(CommandRunState.Succeeded, (await session.Completion).State);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        Assert.IsFalse(h.Snapshot.IsBusy);
    }

    [TestMethod]
    public async Task FailedManualRestartRetainsPreviousBudgetUntilSuccessfulNewCommit()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        h.Clock.Advance(2);
        await h.Count(2);
        await h.FailAndWait();
        var round = h.Snapshot.RoundId;
        h.PreflightFailure = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => h.Coordinator.RestartAsync(h.Request));
        Assert.AreEqual(round, h.Snapshot.RoundId);
        Assert.AreEqual(1, h.Snapshot.Attempts);
        h.PreflightFailure = false;
        await h.Coordinator.RestartAsync(h.Request);
        Assert.AreNotEqual(round, h.Snapshot.RoundId);
        Assert.AreEqual(0, h.Snapshot.Attempts);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task FailedRestartStopCancelsOldRoundAndRequiresExplicitStopBeforeAnyReplacement()
    {
        var h = new Harness();
        await h.Start();
        h.Latest.StopFailure = new IOException("synthetic stop");
        await Assert.ThrowsExactlyAsync<IOException>(() => h.Coordinator.RestartAsync(h.Request));
        Assert.AreEqual(CommandRecoveryError.StopFailed, h.Snapshot.Error);
        Assert.IsTrue(h.Snapshot.IsBusy);
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Coordinator.RestartAsync(h.Request));
        h.Latest.StopFailure = null;
        await h.Coordinator.StopAsync(h.Id);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        await h.Start();
        Assert.HasCount(2, h.Sessions);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task PauseBeforeDeadlineAndNestedConfirmationKeepOriginalRemainingTime()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using (h.Coordinator.PauseAutomaticStarts())
        {
            using (h.Coordinator.PauseAutomaticStarts()) h.Clock.Advance(1);
            Assert.AreEqual(TimeSpan.FromSeconds(1), h.Snapshot.RemainingDelay);
        }
        Assert.HasCount(1, h.Sessions);
        h.Clock.Advance(1);
        await h.Count(2);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task PauseBeginningDuringPreflightBlocksCommitWithoutSpendingBudget()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using var gate = new BarrierGate();
        h.PreflightGate = gate;
        h.Clock.Advance(2);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (h.Coordinator.PauseAutomaticStarts())
        {
            gate.Release();
            await gate.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.HasCount(1, h.Sessions);
            Assert.AreEqual(0, h.Snapshot.Attempts);
        }
        await h.Count(2);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task GroupFirstFailureRemainsVisibleAndCleanupCannotCancelSkippedIndependentWaitOrLaterSession()
    {
        var h = new Harness();
        var independent = await h.Start();
        await h.FailAndWait();
        var groupId = Guid.NewGuid();
        var groupCommand = new CommandDefinition { Id = groupId, Kind = CommandKind.Service };
        var groupSession = (FakeSession)await h.Coordinator.StartAsync(h.Request with { CommandId = groupId, Source = CommandStartSource.Group });
        var observing = CommandGroupExecution.ObserveParallelAsync([new(groupCommand, groupSession.Completion)]);
        groupSession.Finish(CommandRunState.Failed, 9);
        var failure = await observing;
        Assert.AreEqual(ParallelGroupObservationOutcome.CommandFailed, failure.Outcome);
        Assert.AreEqual(9, failure.RunResult!.ExitCode);
        await h.Coordinator.StopAsync(h.Id, independent);
        Assert.AreEqual(CommandRecoveryState.Waiting, h.Snapshot.State);
        h.Clock.Advance(2);
        await h.Count(3);
        await h.Coordinator.StopAsync(h.Id, independent);
        Assert.AreEqual(0, h.Latest.Stops);
        Assert.AreEqual(1, h.Snapshot.Attempts);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task ExitFailureReopensOnlyExplicitManualStartsAndNeverRevivesCancelledRound()
    {
        var h = new Harness();
        await h.Start();
        h.Latest.StopFailure = new IOException("synthetic exit stop");
        await Assert.ThrowsExactlyAsync<IOException>(() => h.Coordinator.CloseAndStopAsync());
        h.Coordinator.ReopenForManualStarts();
        Assert.ThrowsExactly<InvalidOperationException>(() => h.Start());
        h.Latest.StopFailure = null;
        await h.Coordinator.StopAsync(h.Id);
        h.Clock.Advance(1000);
        Assert.HasCount(1, h.Sessions);
        await h.Start();
        Assert.AreEqual(0, h.Snapshot.Attempts);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task EmptyFastExitClosesAllStartSourcesAndAutomaticObservationFailureAborts()
    {
        var empty = new Harness();
        await empty.Coordinator.CloseAndStopAsync();
        foreach (var source in Enum.GetValues<CommandStartSource>())
            Assert.ThrowsExactly<InvalidOperationException>(() => empty.Coordinator.StartAsync(empty.Request with { Source = source }));
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        h.Clock.Advance(2);
        await h.Count(2);
        h.Latest.Fault();
        await h.State(CommandRecoveryState.OwnershipUnconfirmed);
        Assert.AreEqual(1, h.Snapshot.Attempts);
        Assert.AreEqual(CommandRecoveryError.ObservationFailed, h.Snapshot.Error);
        await h.Coordinator.CloseAndStopAsync();
    }

    [TestMethod]
    public async Task SessionCreatedRunsOnlyAfterCommitAndFailureRetainsOwnership()
    {
        var h = new Harness();
        var notices = 0;
        var request = h.Request with { Prepare = () => h.Prepare() with { SessionCreated = session =>
        {
            notices++;
            Assert.AreSame(session, h.Coordinator.GetSnapshot(h.Id)!.Session);
            throw new IOException("synthetic presentation failure");
        } } };
        var session = await h.Coordinator.StartAsync(request);
        Assert.AreEqual(1, notices);
        Assert.IsTrue(h.Coordinator.IsBusy(h.Id));
        Assert.IsTrue(h.Snapshot.LogFinalizationFailed);
        await h.Coordinator.CloseAndStopAsync();
        Assert.IsTrue(session.IsOwnershipReleased);
        h.Coordinator.ReopenForManualStarts();
        h.CreateFailure = new IOException("synthetic creation failure");
        await Assert.ThrowsExactlyAsync<IOException>(() => h.Coordinator.StartAsync(request));
        Assert.AreEqual(1, notices);
    }

    [TestMethod]
    public async Task AutomaticPreflightIsSubmittingAndExitCancelsBeforeInjectedStop()
    {
        var h = new Harness();
        await h.Start();
        await h.FailAndWait();
        using var gate = new BarrierGate();
        h.PreflightGate = gate;
        h.Clock.Advance(2);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(CommandRecoveryState.Starting, h.Snapshot.State);
        Assert.AreEqual(0, h.Snapshot.Attempts);
        await h.Coordinator.CloseAndStopAsync(stopSession: _ => throw new AssertFailedException("无活动会话不应停止"));
        gate.Release();
        await gate.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, h.Sessions);
        Assert.IsFalse(h.Coordinator.IsBusy(h.Id));
    }

    private sealed class Harness
    {
        private readonly ConcurrentQueue<FakeSession> _sessions = new();
        internal Guid Id { get; } = Guid.NewGuid();
        internal ManualTime Clock { get; } = new();
        internal CommandRecoveryCoordinator Coordinator { get; }
        internal BarrierGate? PreflightGate;
        internal BarrierGate? CreateGate;
        internal bool PreflightFailure;
        internal Exception? CreateFailure;
        internal Exception? NextStopFailure;
        internal List<FakeSession> Sessions => _sessions.ToList();
        internal FakeSession Latest => Sessions[^1];
        internal CommandRecoverySnapshot Snapshot => Coordinator.GetSnapshot(Id)!;
        internal CommandStartRequest Request => new(Id, CommandKind.Service, CommandStartSource.Independent, Prepare, true);
        internal Harness() => Coordinator = new((_, _) =>
        {
            CreateGate?.Wait();
            if (CreateFailure is { } failure) throw failure;
            var session = new FakeSession { StopFailure = NextStopFailure };
            _sessions.Enqueue(session);
            return session;
        }, Clock);
        internal CommandStartPlan Prepare()
        {
            PreflightGate?.Wait();
            if (PreflightFailure) throw new IOException("synthetic preflight");
            return new("exit 7", "X:/placeholder");
        }
        internal Task<ICommandRunSession> Start() => Coordinator.StartAsync(Request);
        internal Task State(CommandRecoveryState state) => TcpProbeTests.Until(() => Snapshot.State == state);
        internal Task Count(int count) => TcpProbeTests.Until(() => Sessions.Count == count && Snapshot.State == CommandRecoveryState.Running);
        internal async Task FailAndWait(FakeSession? session = null)
        {
            (session ?? Latest).Finish(CommandRunState.Failed, 7);
            await State(CommandRecoveryState.Waiting);
        }
    }

    private sealed class BarrierGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Exited { get; } = Signal();
        internal void Wait()
        {
            Entered.TrySetResult();
            Assert.IsTrue(_release.Wait(TimeSpan.FromSeconds(10)), "测试屏障没有释放。");
            Exited.TrySetResult();
        }
        internal void Release() => _release.Set();
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }

    internal sealed class FakeSession : ICommandRunSession
    {
        private readonly TaskCompletionSource<CommandRunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CommandRunResult> Completion => _completion.Task;
        public bool IsOwnershipReleased { get; private set; }
        public bool IsStopRequested { get; private set; }
        internal int Stops;
        internal Exception? StopFailure;
        internal void Finish(CommandRunState state, int code)
        {
            IsOwnershipReleased = true;
            _completion.TrySetResult(new(state, code, false));
        }
        internal void Fault() => _completion.SetException(new IOException("synthetic observation"));
        public Task<CommandStopResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            IsStopRequested = true;
            if (StopFailure is { } failure) return Task.FromException<CommandStopResult>(failure);
            cancellationToken.ThrowIfCancellationRequested();
            Finish(CommandRunState.Stopped, 1);
            return Task.FromResult(CommandStopResult.StoppedAfterCtrlC);
        }
        public async ValueTask DisposeAsync() => await StopAsync();
    }

    internal sealed class ManualTime : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<Timer> _timers = [];
        private long _ticks;
        private int _wallOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp()).AddYears(_wallOffset);
        internal void JumpWallClock() => _wallOffset = _wallOffset == 0 ? 20 : 0;
        internal int Pending { get { lock (_sync) return _timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new Timer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(int seconds)
        {
            Timer[] due;
            lock (_sync)
            {
                _ticks += TimeSpan.FromSeconds(seconds).Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due) timer.Due = long.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            internal TimerCallback Callback => callback;
            internal object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return true;
            }
            public void Dispose() { lock (owner._sync) { Due = long.MaxValue; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
