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
    public async Task PreservesUnicodeWorkingDirectoryAndOutputAcrossCapturedStreams()
    {
        using var sandbox = new TemporaryDirectory();
        var workingDirectory = Path.Combine(sandbox.Path, "中文 工作目录");
        Directory.CreateDirectory(workingDirectory);

        await using var session = _runner.Start(Guid.NewGuid(),
            "Write-Output (Get-Location).Path; Write-Output '中文 stdout marker'; [Console]::Error.WriteLine('中文 stderr marker'); exit 0",
            workingDirectory);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        var output = session.GetRecentOutput();

        Assert.AreEqual(CommandRunState.Succeeded, result.State);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(output.Any(line => line.Stream == "stdout" && line.Text.Equals(workingDirectory, StringComparison.OrdinalIgnoreCase)),
            $"Expected working directory '{workingDirectory}', received: {string.Join(" | ", output.Select(line => $"{line.Stream}:{line.Text}"))}");
        Assert.IsTrue(output.Any(line => line.Stream == "stdout" && line.Text == "中文 stdout marker"));
        Assert.IsTrue(output.Any(line => line.Stream == "stderr" && line.Text == "中文 stderr marker"));
    }

    [TestMethod]
    public async Task PowerShellDiagnosticOutputIsCapturedWithoutAnsiControlSequences()
    {
        using var sandbox = new TemporaryDirectory();
        await using var session = _runner.Start(Guid.NewGuid(), "Write-Error 'sample-failure'; exit 2", sandbox.Path);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        var diagnosticOutput = string.Join('\n', session.GetRecentOutput()
            .Where(line => line.Stream == "stderr")
            .Select(line => line.Text));

        Assert.AreEqual(CommandRunState.Failed, result.State);
        Assert.AreEqual(2, result.ExitCode);
        StringAssert.Contains(diagnosticOutput, "sample-failure");
        Assert.DoesNotContain('\u001b', diagnosticOutput);
    }

    [TestMethod]
    public async Task MissingCommandAndUnhandledExceptionAreReportedAsFailures()
    {
        using var sandbox = new TemporaryDirectory();
        await using var missing = _runner.Start(Guid.NewGuid(), "TalosDesk_MissingSyntheticCommand_9F28", sandbox.Path);
        var missingResult = await missing.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(CommandRunState.Failed, missingResult.State);
        Assert.AreNotEqual(0, missingResult.ExitCode);
        Assert.IsTrue(missing.GetRecentOutput().Any(line => line.Stream == "stderr" && line.Text.Contains("TalosDesk_MissingSyntheticCommand_9F28", StringComparison.Ordinal)));

        await using var exception = _runner.Start(Guid.NewGuid(), "throw 'TalosDesk synthetic exception marker';", sandbox.Path);
        var exceptionResult = await exception.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(CommandRunState.Failed, exceptionResult.State);
        Assert.AreNotEqual(0, exceptionResult.ExitCode);
        Assert.IsTrue(exception.GetRecentOutput().Any(line => line.Stream == "stderr" && line.Text.Contains("synthetic exception marker", StringComparison.Ordinal)));
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
    public void MissingPowerShellOnApplicationPathIsReportedClearly()
    {
        using var sandbox = new TemporaryDirectory();

        var exception = Assert.ThrowsExactly<FileNotFoundException>(() =>
            WindowsNative.FindPowerShellPath(sandbox.Path, sandbox.Path));

        StringAssert.Contains(exception.Message, "未在应用启动时继承的 PATH 中找到 PowerShell 7（pwsh.exe）");
        StringAssert.Contains(exception.Message, "PATH");
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
    public async Task RunsAndStopsNativeExecutableFromAnArbitraryToolDirectory()
    {
        using var sandbox = new TemporaryDirectory();
        var toolDirectory = Path.Combine(sandbox.Path, "tool files");
        Directory.CreateDirectory(toolDirectory);
        var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Copy(cmdPath, Path.Combine(toolDirectory, "cmd.exe"));
        const string command = ".\\cmd.exe /d /c 'echo native-ready & ping 127.0.0.1 -n 120 >nul'";

        await using var session = _runner.Start(Guid.NewGuid(), command, toolDirectory);
        var timeout = Stopwatch.StartNew();
        while (!session.GetRecentOutput().Any(line => line.Text.Contains("native-ready", StringComparison.Ordinal)) &&
               !session.Completion.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        Assert.IsFalse(session.Completion.IsCompleted, "The native process exited before it could be stopped.");
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Text.Contains("native-ready", StringComparison.Ordinal)),
            "The native process did not produce its readiness marker.");

        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.AreEqual(CommandRunState.Stopped, result.State);
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
    public async Task HostExitClosesItsOwnedCommandJob()
    {
        using var sandbox = new TemporaryDirectory();
        var assemblyPath = typeof(CommandRunner).Assembly.Location.Replace("'", "''", StringComparison.Ordinal);
        var workingDirectory = sandbox.Path.Replace("'", "''", StringComparison.Ordinal);
        var scriptPath = Path.Combine(sandbox.Path, "synthetic-host.ps1");
        var script = $$"""
            Add-Type -Path '{{assemblyPath}}'
            $runner = [TalosDesk.Core.Processes.CommandRunner]::new()
            $session = $runner.Start([guid]::NewGuid(), 'Write-Output "target-pid:$PID"; while ($true) { Start-Sleep -Seconds 1 }', '{{workingDirectory}}')
            while ($true) {
                $marker = $session.GetRecentOutput() | Where-Object { $_.Text.StartsWith('target-pid:') } | Select-Object -First 1
                if ($null -ne $marker) {
                    [Console]::Out.WriteLine($marker.Text)
                    [Console]::Out.Flush()
                    break
                }
                Start-Sleep -Milliseconds 20
            }
            while ($true) { Start-Sleep -Seconds 1 }
            """;
        await File.WriteAllTextAsync(scriptPath, script);

        var start = new ProcessStartInfo
        {
            FileName = WindowsNative.FindPowerShellPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, sandbox.Path),
            WorkingDirectory = sandbox.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);

        using var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the synthetic host.");
        Process? target = null;
        try
        {
            var marker = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (marker is null)
                throw new AssertFailedException($"The synthetic host exited before reporting its command: {await host.StandardError.ReadToEndAsync()}");
            Assert.IsTrue(marker.StartsWith("target-pid:", StringComparison.Ordinal), $"Unexpected host output: {marker}");
            Assert.IsTrue(int.TryParse(marker.AsSpan("target-pid:".Length), out var targetPid), $"Invalid target PID: {marker}");
            target = Process.GetProcessById(targetPid);
            Assert.IsFalse(target.HasExited, "The command exited before the synthetic host was killed.");

            host.Kill();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(target.HasExited, "Closing the host must close its command Job and stop the owned process.");
        }
        finally
        {
            await KillOwnedProcessIfRunningAsync(host);
            if (target is not null)
            {
                await KillOwnedProcessIfRunningAsync(target);
                target.Dispose();
            }
        }
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

    private static async Task KillOwnedProcessIfRunningAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // 进程可能在清理时自行退出。
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // 进程可能在结束检查与终止请求之间退出。
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
