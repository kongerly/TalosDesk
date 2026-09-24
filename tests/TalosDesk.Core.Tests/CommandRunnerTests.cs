using System.ComponentModel;
using System.Diagnostics;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CommandRunnerTests
{
    private readonly CommandRunner _runner = new();

    [TestMethod]
    public async Task RunsFromConfiguredWorkingDirectoryAndCapturesBothStreamsAndExitCode()
    {
        using var sandbox = new TemporaryDirectory();
        var normalizedDirectory = Path.GetFullPath(sandbox.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await using var session = _runner.Start(Guid.NewGuid(), "Write-Output (Get-Location).Path; [Console]::Error.WriteLine('stderr marker'); exit 7", sandbox.Path);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(CommandRunState.Failed, result.State);
        Assert.AreEqual(7, result.ExitCode);
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Stream == "stdout" && line.Text.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase)),
            $"Expected working directory '{sandbox.Path}', received: {string.Join(" | ", session.GetRecentOutput().Select(line => $"{line.Stream}:{line.Text}"))}");
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Stream == "stderr" && line.Text.Contains("stderr marker", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FailedStartDoesNotReserveCommandIdAndCanBeRetried()
    {
        using var sandbox = new TemporaryDirectory();
        var workingDirectory = Path.Combine(sandbox.Path, "created-after-failure");
        var commandId = Guid.NewGuid();

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => _runner.Start(commandId, "Write-Output 'retry marker'", workingDirectory));
        Assert.IsFalse(_runner.IsRunning(commandId));

        Directory.CreateDirectory(workingDirectory);
        await using var session = _runner.Start(commandId, "Write-Output 'retry marker'", workingDirectory);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(CommandRunState.Succeeded, result.State);
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Text.Contains("retry marker", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PowerShellCreateFailureReleasesPartiallyCreatedStartupResources()
    {
        using var sandbox = new TemporaryDirectory();
        var missingExecutable = Path.Combine(sandbox.Path, "missing-pwsh.exe");

        Assert.ThrowsExactly<Win32Exception>(() => WindowsNative.StartPowerShell("exit 0", sandbox.Path, missingExecutable));
        Assert.ThrowsExactly<Win32Exception>(() => WindowsNative.StartPowerShell("exit 0", sandbox.Path, missingExecutable));
    }

    [TestMethod]
    public async Task CtrlCStopsRunningCommandAndTheSameCommandCanBeRestarted()
    {
        using var sandbox = new TemporaryDirectory();
        var commandId = Guid.NewGuid();
        await using var session = _runner.Start(commandId, "while ($true) { Start-Sleep -Seconds 1 }", sandbox.Path);
        await Task.Delay(500);
        Assert.ThrowsExactly<InvalidOperationException>(() => _runner.Start(commandId, "exit 0", sandbox.Path));

        var stopResult = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(CommandStopResult.StoppedAfterCtrlC, stopResult);
        Assert.AreEqual(CommandRunState.Stopped, result.State);
        Assert.IsFalse(result.WasForceTerminated);

        await using var restarted = _runner.Start(commandId, "Write-Output 'restart marker'", sandbox.Path);
        var restartedResult = await restarted.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(CommandRunState.Succeeded, restartedResult.State);
        Assert.IsTrue(restarted.GetRecentOutput().Any(line => line.Text.Contains("restart marker", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FailedForceStopKeepsCommandOwnedAndCanBeRetried()
    {
        using var sandbox = new TemporaryDirectory();
        var runner = new CommandRunner(new FailFirstForceStopOperations());
        var commandId = Guid.NewGuid();
        var command = "$child = Start-Process -FilePath 'pwsh.exe' -WindowStyle Hidden -PassThru -ArgumentList '-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 120'; Write-Output \"child-pid:$($child.Id)\"; while ($true) { Start-Sleep -Seconds 1 }";
        await using var session = runner.Start(commandId, command, sandbox.Path);
        var childId = await WaitForChildIdAsync(session);
        Assert.IsTrue(IsProcessAlive(childId), $"Child process {childId} exited before the synthetic stop failure.");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(runner.IsRunning(commandId), "A failed stop must leave the command tracked while its process group is still alive.");
        Assert.IsTrue(IsProcessAlive(childId), "A failed stop must not silently kill or forget the synthetic child.");
        Assert.ThrowsExactly<InvalidOperationException>(() => runner.Start(commandId, "exit 0", sandbox.Path));

        var stopResult = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(12));
        await WaitUntilProcessExitsAsync(childId);

        Assert.AreEqual(CommandStopResult.ForceTerminated, stopResult);
        Assert.AreEqual(CommandRunState.Stopped, result.State);
        Assert.IsTrue(result.WasForceTerminated);
    }

    [TestMethod]
    public async Task ForcedStopIsNotReportedAsSuccessfulUntilGroupExitIsConfirmed()
    {
        using var sandbox = new TemporaryDirectory();
        var runner = new CommandRunner(new FailExitConfirmationOperations());
        await using var session = runner.Start(Guid.NewGuid(), "while ($true) { Start-Sleep -Seconds 1 }", sandbox.Path);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(CommandRunState.Stopped, result.State);
        Assert.IsTrue(result.WasForceTerminated);
    }

    [TestMethod]
    public async Task ThrowingOutputSubscriberDoesNotInterruptOutputDraining()
    {
        using var sandbox = new TemporaryDirectory();
        await using var session = _runner.Start(Guid.NewGuid(), "Write-Output 'subscriber marker'", sandbox.Path,
            (_, _) => throw new InvalidOperationException("Synthetic subscriber failure."));

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(CommandRunState.Succeeded, result.State);
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Text.Contains("subscriber marker", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task KeepsOnlyTheMostRecentTenThousandOutputLines()
    {
        using var sandbox = new TemporaryDirectory();
        await using var session = _runner.Start(Guid.NewGuid(), "1..10005 | ForEach-Object { Write-Output \"line-$_\" }", sandbox.Path);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var output = session.GetRecentOutput();

        Assert.AreEqual(CommandRunState.Succeeded, result.State);
        Assert.HasCount(10_000, output);
        Assert.IsTrue(output[0].Text.Contains("line-6", StringComparison.Ordinal));
        Assert.IsTrue(output[^1].Text.Contains("line-10005", StringComparison.Ordinal));
    }

    private static async Task<int> WaitForChildIdAsync(CommandRunSession session)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var line = session.GetRecentOutput().FirstOrDefault(entry => entry.Text.StartsWith("child-pid:", StringComparison.Ordinal));
            if (line is not null && int.TryParse(line.Text.AsSpan("child-pid:".Length), out var processId)) return processId;
            if (session.Completion.IsCompleted) break;
            await Task.Delay(50);
        }

        Assert.Fail("The command did not report its child process ID.");
        return 0;
    }

    private static async Task WaitUntilProcessExitsAsync(int processId)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Child process {processId} remained alive after its job was stopped.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FailFirstForceStopOperations : ICommandStopOperations
    {
        private int _terminationAttempts;

        public bool TrySendCtrlC(int processId) => false;

        public Task<bool> WaitForCommandGroupExitAsync(SafeJobHandle job, TimeSpan timeout, CancellationToken cancellationToken) =>
            Volatile.Read(ref _terminationAttempts) < 2
                ? Task.FromResult(false)
                : WindowsNative.WaitForCommandGroupExitAsync(job, timeout, cancellationToken);

        public void TerminateJob(SafeJobHandle job)
        {
            if (Interlocked.Increment(ref _terminationAttempts) == 1)
            {
                throw new InvalidOperationException("Synthetic force-stop failure.");
            }

            WindowsNative.TerminateJob(job);
        }
    }

    private sealed class FailExitConfirmationOperations : ICommandStopOperations
    {
        private int _waitCount;

        public bool TrySendCtrlC(int processId) => false;

        public Task<bool> WaitForCommandGroupExitAsync(SafeJobHandle job, TimeSpan timeout, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _waitCount) <= 2
                ? Task.FromResult(false)
                : WindowsNative.WaitForCommandGroupExitAsync(job, timeout, cancellationToken);

        public void TerminateJob(SafeJobHandle job) => WindowsNative.TerminateJob(job);
    }
}
