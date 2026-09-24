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
        var expectedDirectory = Path.GetFullPath(Path.GetTempPath());
        var normalizedDirectory = expectedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var session = _runner.Start(Guid.NewGuid(), "Write-Output (Get-Location).Path; [Console]::Error.WriteLine('stderr marker'); exit 7", expectedDirectory);
        await Task.Delay(500);
        Assert.IsNotEmpty(session.GetRecentOutput(), "No command output arrived. Check the PowerShell process launch and pipe handles.");
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(CommandRunState.Failed, result.State);
        Assert.AreEqual(7, result.ExitCode);
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Stream == "stdout" && line.Text.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase)),
            $"Expected working directory '{expectedDirectory}', received: {string.Join(" | ", session.GetRecentOutput().Select(line => $"{line.Stream}:{line.Text}"))}");
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Stream == "stderr" && line.Text.Contains("stderr marker", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CtrlCStopsRunningCommandAndSameCommandCannotStartTwice()
    {
        var commandId = Guid.NewGuid();
        var session = _runner.Start(commandId, "while ($true) { Start-Sleep -Seconds 1 }", Environment.CurrentDirectory);
        await Task.Delay(500);
        Assert.ThrowsExactly<InvalidOperationException>(() => _runner.Start(commandId, "exit 0", Environment.CurrentDirectory));

        var stopResult = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(CommandStopResult.StoppedAfterCtrlC, stopResult);
        Assert.AreEqual(CommandRunState.Stopped, result.State);
        Assert.IsFalse(result.WasForceTerminated);
    }

    [TestMethod]
    public async Task ForceStopRemovesDescendantInDifferentConsole()
    {
        var command = "$child = Start-Process -FilePath 'pwsh.exe' -WindowStyle Hidden -PassThru -ArgumentList '-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 120'; Write-Output \"child-pid:$($child.Id)\"; while ($true) { Start-Sleep -Seconds 1 }";
        var session = _runner.Start(Guid.NewGuid(), command, Environment.CurrentDirectory);

        var childId = await WaitForChildIdAsync(session);
        Assert.IsTrue(IsProcessAlive(childId), $"Child process {childId} exited before stop. Output: {string.Join(" | ", session.GetRecentOutput().Select(line => line.Text))}");
        await Task.Delay(2_000);
        var stopResult = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(12));
        await WaitUntilProcessExitsAsync(childId);

        Assert.AreEqual(CommandStopResult.ForceTerminated, stopResult);
        Assert.AreEqual(CommandRunState.Stopped, result.State);
        Assert.IsTrue(result.WasForceTerminated);
    }

    [TestMethod]
    public async Task KeepsOnlyTheMostRecentTenThousandOutputLines()
    {
        var session = _runner.Start(Guid.NewGuid(), "1..10005 | ForEach-Object { Write-Output \"line-$_\" }", Environment.CurrentDirectory);
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

}
