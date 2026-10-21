using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CommandRecoveryProcessTests
{
    [TestMethod]
    public async Task RealSessionsHaveDistinctProcessesProbesAndLogsWithoutReturningBudgetOrLeakingSyntheticSecret()
    {
        using var sandbox = new Sandbox();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var probe = new TcpProbeConfiguration { Port = ((IPEndPoint)listener.LocalEndpoint).Port, IntervalSeconds = 1, FailureThreshold = 1 };
        var clock = new CommandRecoveryCoordinatorTests.ManualTime();
        var runner = new CommandRunner();
        var coordinator = new CommandRecoveryCoordinator(runner, clock);
        var id = Guid.NewGuid();
        var project = Guid.NewGuid();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "isolated-workspace.json"));
        store.Initialize();
        var sessions = new List<CommandRunSession>();
        var runs = new ConcurrentQueue<RunLogInfo>();
        var index = 0;
        const string syntheticSecret = "TalosDesk-synthetic-recovery-secret-23478";
        var request = new CommandStartRequest(id, CommandKind.Service, CommandStartSource.Independent, () =>
        {
            var releaseFile = $"release-{Interlocked.Increment(ref index)}";
            return new($"Write-Output $env:TALOS_SYNTHETIC; Write-Output \"pid:$PID\"; while (!(Test-Path '{releaseFile}')) {{ Start-Sleep -Milliseconds 20 }}; exit 7",
                sandbox.Path, Environment: new(new Dictionary<string, string> { ["TALOS_SYNTHETIC"] = syntheticSecret }, [syntheticSecret]), TcpProbe: probe);
        }, true, (session, result) =>
        {
            var writer = store.Begin(project, id, DateTimeOffset.UtcNow);
            foreach (var output in ((CommandRunSession)session).GetRecentOutput()) store.Append(writer, output);
            store.Complete(writer, result);
            runs.Enqueue(writer.Info);
            return Task.CompletedTask;
        });
        try
        {
            var first = (CommandRunSession)await coordinator.StartAsync(request);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var session = attempt == 0 ? first : (CommandRunSession)coordinator.GetSnapshot(id)!.Session!;
                sessions.Add(session);
                await TcpProbeTests.Until(() => session.TcpProbe.State == TcpProbeState.Passed);
                await TcpProbeTests.Until(() => session.GetRecentOutput().Any(line => line.Text.StartsWith("pid:", StringComparison.Ordinal)));
                clock.Advance(100_000);
                Assert.AreEqual(attempt, coordinator.GetSnapshot(id)!.Attempts);
                if (attempt == 0)
                {
                    listener.Stop();
                    await TcpProbeTests.Until(() => session.TcpProbe.State == TcpProbeState.Unreachable);
                    Assert.IsFalse(session.Completion.IsCompleted);
                    Assert.AreEqual(0, coordinator.GetSnapshot(id)!.Attempts);
                    listener.Start();
                    await TcpProbeTests.Until(() => session.TcpProbe.State == TcpProbeState.Passed);
                }
                await File.WriteAllTextAsync(Path.Combine(sandbox.Path, $"release-{attempt + 1}"), "synthetic");
                var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.AreEqual(CommandRunState.Failed, result.State);
                Assert.AreEqual(7, result.ExitCode);
                Assert.IsTrue(session.IsOwnershipReleased);
                Assert.AreEqual(TcpProbeState.Inactive, session.TcpProbe.State);
                var expected = attempt == 3 ? CommandRecoveryState.Exhausted : CommandRecoveryState.Waiting;
                await TcpProbeTests.Until(() => coordinator.GetSnapshot(id)!.State == expected);
                if (attempt < 3)
                {
                    Assert.AreEqual(TimeSpan.FromSeconds(2 << attempt), coordinator.GetSnapshot(id)!.RemainingDelay);
                    clock.Advance(2 << attempt);
                    await TcpProbeTests.Until(() => coordinator.GetSnapshot(id)!.State == CommandRecoveryState.Running);
                }
            }
            Assert.HasCount(4, runs);
            Assert.HasCount(4, runs.Select(run => run.RunId).Distinct().ToArray());
            Assert.IsTrue(runs.All(run => run.State == "Failed" && run.ExitCode == 7));
            Assert.HasCount(4, sessions.Select(session => session.GetRecentOutput().Single(line => line.Text.StartsWith("pid:", StringComparison.Ordinal)).Text).Distinct().ToArray());
            foreach (var path in Directory.EnumerateFiles(store.RootPath, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(syntheticSecret, await File.ReadAllTextAsync(path));
            foreach (var session in sessions) Assert.IsFalse(session.GetRecentOutput().Any(line => line.Text.Contains(syntheticSecret, StringComparison.Ordinal)));
            clock.Advance(1000);
            Assert.AreEqual(4, index);
            Assert.IsFalse(coordinator.IsBusy(id));
        }
        finally
        {
            await coordinator.CloseAndStopAsync();
            foreach (var session in sessions) await session.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RealJobChildBlocksRecoveryUntilExitAndStoppingCannotAffectUnrelatedSession()
    {
        using var sandbox = new Sandbox();
        var clock = new CommandRecoveryCoordinatorTests.ManualTime();
        var runner = new CommandRunner();
        var coordinator = new CommandRecoveryCoordinator(runner, clock);
        var id = Guid.NewGuid();
        var childRelease = Path.Combine(sandbox.Path, "release-child.ps1");
        await File.WriteAllTextAsync(childRelease, "while (!(Test-Path 'child-release')) { Start-Sleep -Milliseconds 20 }");
        var shell = WindowsNative.FindPowerShellPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, sandbox.Path).Replace("'", "''", StringComparison.Ordinal);
        await using var unrelated = runner.Start(Guid.NewGuid(), "Write-Output 'unrelated-ready'; while ($true) { Start-Sleep -Milliseconds 50 }", sandbox.Path);
        var started = 0;
        var request = new CommandStartRequest(id, CommandKind.Service, CommandStartSource.Independent, () =>
        {
            Interlocked.Increment(ref started);
            return new($"$child = Start-Process -FilePath '{shell}' -WindowStyle Hidden -PassThru -WorkingDirectory (Get-Location).Path -ArgumentList '-NoProfile -NonInteractive -File release-child.ps1'; Write-Output \"child-pid:$($child.Id)\"; exit 7", sandbox.Path);
        }, true);
        try
        {
            var session = (CommandRunSession)await coordinator.StartAsync(request);
            await TcpProbeTests.Until(() => session.GetRecentOutput().Any(line => line.Text.StartsWith("child-pid:", StringComparison.Ordinal)));
            var childId = int.Parse(session.GetRecentOutput().Single(line => line.Text.StartsWith("child-pid:", StringComparison.Ordinal)).Text.AsSpan("child-pid:".Length), System.Globalization.CultureInfo.InvariantCulture);
            using var child = Process.GetProcessById(childId);
            Assert.IsFalse(child.HasExited);
            Assert.IsFalse(session.Completion.IsCompleted);
            clock.Advance(1000);
            Assert.AreEqual(1, started);
            await File.WriteAllTextAsync(Path.Combine(sandbox.Path, "child-release"), "synthetic");
            Assert.AreEqual(7, (await session.Completion.WaitAsync(TimeSpan.FromSeconds(12))).ExitCode);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await TcpProbeTests.Until(() => coordinator.GetSnapshot(id)!.State == CommandRecoveryState.Waiting);
            await coordinator.StopAsync(id);
            clock.Advance(1000);
            Assert.AreEqual(1, started);
            Assert.IsFalse(unrelated.Completion.IsCompleted);
        }
        finally { await coordinator.CloseAndStopAsync(); }
    }

    [TestMethod]
    public async Task NativeObservationFailureRetainsJobAndRunnerBlocksDuplicatesUntilStopConfirmsExit()
    {
        using var sandbox = new Sandbox();
        var observation = new FailingObservation();
        var runner = new CommandRunner(WindowsCommandStopOperations.Instance, observation);
        var id = Guid.NewGuid();
        var session = runner.Start(id, "Write-Output \"owned-pid:$PID\"; while ($true) { Start-Sleep -Milliseconds 50 }", sandbox.Path);
        try
        {
            await TcpProbeTests.Until(() => session.GetRecentOutput().Any(line => line.Text.StartsWith("owned-pid:", StringComparison.Ordinal)));
            var pid = int.Parse(session.GetRecentOutput().Single(line => line.Text.StartsWith("owned-pid:", StringComparison.Ordinal)).Text.AsSpan("owned-pid:".Length), System.Globalization.CultureInfo.InvariantCulture);
            using var owned = Process.GetProcessById(pid);
            observation.Fail.SetResult();
            await Assert.ThrowsExactlyAsync<IOException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(session.IsOwnershipReleased);
            Assert.IsTrue(runner.IsRunning(id));
            Assert.IsFalse(owned.HasExited);
            Assert.ThrowsExactly<InvalidOperationException>(() => runner.Start(id, "exit 0", sandbox.Path));
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(session.IsOwnershipReleased);
            Assert.IsFalse(runner.IsRunning(id));
            observation.Enabled = false;
            await using var replacement = runner.Start(id, "exit 0", sandbox.Path);
            Assert.AreEqual(CommandRunState.Succeeded, (await replacement.Completion).State);
        }
        finally
        {
            observation.Fail.TrySetResult();
            await session.StopAsync();
            await Assert.ThrowsExactlyAsync<IOException>(() => session.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task FailedCreationOwnershipUsesOnlyItsExactHandlesAndConfirmsExitBeforeRelease()
    {
        using var sandbox = new Sandbox();
        var started = WindowsNative.StartPowerShell("while ($true) { Start-Sleep -Milliseconds 50 }", sandbox.Path);
        await using var ownership = new FailedCommandOwnership(started.Job, started.Process, true);
        using var owned = Process.GetProcessById(started.ProcessId);
        using var stdout = started.Stdout;
        using var stderr = started.Stderr;
        _ = ownership.Completion.Exception;
        Assert.IsFalse(ownership.IsOwnershipReleased);
        await ownership.StopAsync();
        await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await stdout.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await stderr.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(ownership.IsOwnershipReleased);
        Assert.AreEqual(CommandStopResult.AlreadyExited, await ownership.StopAsync());
    }

    private sealed class FailingObservation : ICommandObservationOperations
    {
        internal TaskCompletionSource Fail { get; } = CommandRecoveryCoordinatorTests.Signal();
        internal bool Enabled = true;
        public async Task WaitForProcessAsync(SafeProcessHandle process)
        {
            if (Enabled) { await Fail.Task; throw new IOException("synthetic observation"); }
            await WindowsCommandObservationOperations.Instance.WaitForProcessAsync(process);
        }
        public Task WaitForGroupAsync(SafeJobHandle job) => WindowsCommandObservationOperations.Instance.WaitForGroupAsync(job);
        public int GetExitCode(SafeProcessHandle process) => WindowsNative.GetExitCode(process);
    }

    private sealed class Sandbox : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TalosDesk.RecoveryTests", Guid.NewGuid().ToString("N"));
        internal Sandbox() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
