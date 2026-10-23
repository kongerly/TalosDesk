using System.Diagnostics;
using System.Text;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CmdProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CapturesUtf8ChannelsEnvironmentAndRedactedHistory()
    {
        using var sandbox = new Sandbox();
        var logs = new RunLogStore(Path.Combine(sandbox.Root, "workspace.json"));
        logs.Initialize();
        var writer = logs.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        var environment = new CommandRunEnvironment(new Dictionary<string, string>
        {
            ["SYNTHETIC_VAR"] = "中文 环境!100%", ["SYNTHETIC_SECRET"] = "synthetic-cmd-private-7539"
        }, ["synthetic-cmd-private-7539"]);
        await using var session = new CommandRunner().Start(writer.Info.CommandId,
            "echo 中文标准& echo 中文诊断 1>&2 & echo %SYNTHETIC_VAR%& echo %SYNTHETIC_SECRET%& exit /b 9",
            sandbox.Root, (_, line) => logs.Append(writer, line), environment, shell: CommandShell.Cmd);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        logs.Complete(writer, result);
        Assert.AreEqual(9, result.ExitCode);
        Assert.AreEqual(CommandRunState.Failed, result.State);
        var output = session.GetRecentOutput();
        Assert.IsTrue(output.Any(line => line.Stream == "stdout" && line.Text == "中文标准"));
        Assert.IsTrue(output.Any(line => line.Stream == "stderr" && line.Text.TrimEnd() == "中文诊断"));
        Assert.IsTrue(output.Any(line => line.Text == "中文 环境!100%"));
        Assert.IsFalse(string.Join('\n', output.Select(line => line.Text)).Contains(environment.SensitiveValues[0], StringComparison.Ordinal));
        Assert.IsFalse(string.Join('\n', logs.ReadTail(writer.Info, "stdout")).Contains(environment.SensitiveValues[0], StringComparison.Ordinal));
        Assert.IsNull(Environment.GetEnvironmentVariable("SYNTHETIC_VAR"));
    }

    [TestMethod]
    public async Task QuotedBatchAndExecutablePreserveChineseSpaceMetacharactersAndExitCode()
    {
        using var sandbox = new Sandbox();
        var script = Path.Combine(sandbox.Root, "脚本 & (空格).cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\necho \"%~1\"\r\necho \"%CD%\"\r\nexit /b 7\r\n", new UTF8Encoding(false));
        await using var batch = new CommandRunner().Start(Guid.NewGuid(),
            "call \"脚本 & (空格).cmd\" \"中文 & 参数!100%\"", sandbox.Root, shell: CommandShell.Cmd);
        Assert.AreEqual(7, (await batch.Completion.WaitAsync(TimeSpan.FromSeconds(15))).ExitCode);
        Assert.IsTrue(batch.GetRecentOutput().Any(line => line.Text == "\"中文 & 参数!100%\""));
        Assert.IsTrue(batch.GetRecentOutput().Any(line => line.Text == "\"" + sandbox.Root + "\""));
        var executable = Path.Combine(sandbox.Root, "工具 空格.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), executable);
        await using var native = new CommandRunner().Start(Guid.NewGuid(),
            $"\"{executable}\" /d /s /c \"echo 中文原生& exit /b 6\"", sandbox.Root, shell: CommandShell.Cmd);
        Assert.AreEqual(6, (await native.Completion.WaitAsync(TimeSpan.FromSeconds(15))).ExitCode);
        Assert.IsTrue(native.GetRecentOutput().Any(line => line.Text == "中文原生"));
    }

    [TestMethod]
    public async Task CtrlCStopsOwnedCmdAndManualRestartUsesFreshSession()
    {
        using var sandbox = new Sandbox();
        var runner = new CommandRunner();
        var id = Guid.NewGuid();
        var pwsh = WindowsNative.FindPowerShellPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, sandbox.Root);
        await using var session = runner.Start(id, $"\"{pwsh}\" -NoProfile -NonInteractive -Command \"Write-Output 'cmd-ready'; while ($true) {{ Start-Sleep -Milliseconds 100 }}\"", sandbox.Root, shell: CommandShell.Cmd);
        await Until(session, "Cmd Ctrl+C 子进程", output => output.Any(line => line.Text == "cmd-ready"));
        Assert.ThrowsExactly<InvalidOperationException>(() => runner.Start(id, "exit /b 0", sandbox.Root, shell: CommandShell.Cmd));
        Assert.AreEqual(CommandStopResult.StoppedAfterCtrlC, await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.AreEqual(CommandRunState.Stopped, (await session.Completion).State);
        Assert.IsTrue(session.IsOwnershipReleased);
        await using var restarted = runner.Start(id, "echo restarted& exit /b 0", sandbox.Root, shell: CommandShell.Cmd);
        Assert.AreEqual(0, (await restarted.Completion.WaitAsync(TimeSpan.FromSeconds(15))).ExitCode);
        Assert.IsTrue(restarted.GetRecentOutput().Any(line => line.Text == "restarted"));
    }

    [TestMethod]
    public async Task TimeoutForceStopsOnlyOwnedChildrenAndKeepsIndependentSentinelAlive()
    {
        using var sandbox = new Sandbox();
        var pwsh = WindowsNative.FindPowerShellPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, sandbox.Root);
        await File.WriteAllTextAsync(Path.Combine(sandbox.Root, "ignore.ps1"), "Write-Output 'phase:ignore-script-started'; Add-Type -ErrorAction Stop 'using System; using System.Runtime.InteropServices; public static class IgnoreCtrl { [DllImport(\"kernel32.dll\")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add); }'; Write-Output 'phase:ignore-handler-compiled'; if(-not [IgnoreCtrl]::SetConsoleCtrlHandler([IntPtr]::Zero,$true)){throw 'Synthetic Ctrl+C handler registration failed.'}; Write-Output 'phase:ignore-handler-registered'; Write-Output \"child:$PID\"; while($true){Start-Sleep -Milliseconds 100}");
        await using var sentinel = new CommandRunner().Start(Guid.NewGuid(), "Write-Output 'sentinel-ready'; while($true){Start-Sleep -Milliseconds 100}", sandbox.Root);
        await Until(sentinel, "独立哨兵", output => output.Any(line => line.Text == "sentinel-ready"));
        await using var session = new CommandRunner().Start(Guid.NewGuid(), $"\"{pwsh}\" -NoProfile -File ignore.ps1", sandbox.Root, shell: CommandShell.Cmd);
        await Until(session, "忽略 Ctrl+C 的 Cmd 子进程", output => output.Any(line => line.Text.StartsWith("child:", StringComparison.Ordinal)));
        var childId = int.Parse(session.GetRecentOutput().Single(line => line.Text.StartsWith("child:", StringComparison.Ordinal)).Text[6..], System.Globalization.CultureInfo.InvariantCulture);
        using var child = Process.GetProcessById(childId);
        Assert.AreEqual(CommandStopResult.ForceTerminated, await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.IsTrue((await session.Completion).WasForceTerminated);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(session.IsOwnershipReleased);
        Assert.IsFalse(sentinel.Completion.IsCompleted);
        await sentinel.StopAsync();
        await sentinel.Completion;
    }

    [TestMethod]
    public async Task ReadinessFailureReportsExitedCmdWithItsOutput()
    {
        using var sandbox = new Sandbox();
        await using var session = new CommandRunner().Start(Guid.NewGuid(),
            "echo synthetic-start& echo synthetic-diagnostic 1>&2 & exit /b 7", sandbox.Root, shell: CommandShell.Cmd);

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            Until(session, "合成提前退出", output => output.Any(line => line.Text == "never-ready")));

        StringAssert.Contains(failure.Message, "阶段：合成提前退出");
        StringAssert.Contains(failure.Message, "命令已提前结束");
        StringAssert.Contains(failure.Message, "State=Failed; ExitCode=7");
        StringAssert.Contains(failure.Message, "stdout: synthetic-start");
        StringAssert.Contains(failure.Message, "stderr: synthetic-diagnostic");
        Assert.IsTrue(session.IsOwnershipReleased);
        Assert.IsFalse(session.IsStopRequested);
    }

    [TestMethod]
    public async Task ReadinessTimeoutKeepsRunningSessionAndBoundsRedactedDiagnostics()
    {
        using var sandbox = new Sandbox();
        const string variable = "TALOSDESK_SYNTHETIC_READY_5A31";
        const string secret = "synthetic-readiness-secret";
        var environment = new CommandRunEnvironment(new Dictionary<string, string> { [variable] = secret }, [secret]);
        var script = $"0..19 | ForEach-Object {{ Write-Output ('synthetic-line-' + $_ + ':' + $env:{variable} + ':' + ('x' * 500)) }}; [Console]::Error.WriteLine('synthetic-diagnostic'); Write-Output 'diagnostic-ready'; while($true) {{ Start-Sleep -Milliseconds 100 }}";
        await using var session = new CommandRunner().Start(Guid.NewGuid(), script, sandbox.Root, runEnvironment: environment);
        await Until(session, "诊断样本就绪", output => output.Any(line => line.Text == "diagnostic-ready") &&
            output.Any(line => line.Stream == "stderr" && line.Text == "synthetic-diagnostic"));

        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            Until(session, "合成等待超时", output => output.Any(line => line.Text == "never-ready"), TimeSpan.Zero));

        StringAssert.Contains(failure.Message, "阶段：合成等待超时");
        StringAssert.Contains(failure.Message, "等待超时");
        StringAssert.Contains(failure.Message, "Completion=");
        StringAssert.Contains(failure.Message, "OwnershipReleased=False");
        StringAssert.Contains(failure.Message, "StopRequested=False");
        StringAssert.Contains(failure.Message, "synthetic-line-19:[已隐藏]");
        Assert.IsFalse(failure.Message.Contains("synthetic-line-0:", StringComparison.Ordinal));
        Assert.IsFalse(failure.Message.Contains(secret, StringComparison.Ordinal));
        Assert.IsLessThanOrEqualTo(4096, failure.Message.Length);
        foreach (var line in failure.Message.Split(Environment.NewLine).Where(line =>
            line.StartsWith("stdout: ", StringComparison.Ordinal) || line.StartsWith("stderr: ", StringComparison.Ordinal)))
            Assert.IsLessThanOrEqualTo(256, line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Length);
        Assert.IsFalse(session.Completion.IsCompleted);
        Assert.IsFalse(session.IsStopRequested);
        await session.StopAsync();
        Assert.AreEqual(CommandRunState.Stopped, (await session.Completion).State);
    }

    [TestMethod]
    public async Task ReadyMarkerInCompletedSessionStillSatisfiesReadiness()
    {
        using var sandbox = new Sandbox();
        await using var session = new CommandRunner().Start(Guid.NewGuid(), "echo already-ready& exit /b 0", sandbox.Root, shell: CommandShell.Cmd);
        Assert.AreEqual(0, (await session.Completion.WaitAsync(TimeSpan.FromSeconds(15))).ExitCode);

        await Until(session, "已完成且已就绪", output => output.Any(line => line.Text == "already-ready"));

        Assert.IsTrue(session.IsOwnershipReleased);
        Assert.IsFalse(session.IsStopRequested);
    }

    [TestMethod]
    public async Task MaximumSupportedSingleLineExecutesWithoutCmdTruncation()
    {
        using var sandbox = new Sandbox();
        const string prefix = "echo ";
        const string suffix = "& exit /b 0";
        var payload = new string('x', 7000 - prefix.Length - suffix.Length);
        await using var session = new CommandRunner().Start(Guid.NewGuid(), prefix + payload + suffix, sandbox.Root, shell: CommandShell.Cmd);
        Assert.AreEqual(0, (await session.Completion.WaitAsync(TimeSpan.FromSeconds(15))).ExitCode);
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Text == payload));
    }

    [TestMethod]
    public void InvalidShellOrMultilineFailsBeforeStartingAndDoesNotReserveId()
    {
        using var sandbox = new Sandbox();
        var runner = new CommandRunner();
        var id = Guid.NewGuid();
        foreach (var command in new[] { "echo one\necho two", "echo one\recho two", "echo \0", new string('a', 7001) })
            Assert.ThrowsExactly<InvalidDataException>(() => runner.Start(id, command, sandbox.Root, shell: CommandShell.Cmd));
        Assert.ThrowsExactly<InvalidDataException>(() => runner.Start(id, "exit 0", sandbox.Root, shell: (CommandShell)99));
        Assert.IsFalse(runner.IsRunning(id));
    }

    private async Task Until(CommandRunSession session, string phase, Func<IReadOnlyList<CommandOutput>, bool> condition,
        TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var output = session.GetRecentOutput();
            if (condition(output)) return;
            string? reason = null;
            if (session.Completion.IsCompleted)
            {
                // 输出可能在第一次快照后才排空，完成状态不能抢在就绪标记之前判失败。
                output = session.GetRecentOutput();
                if (condition(output)) return;
                try
                {
                    var result = await session.Completion;
                    reason = $"命令已提前结束；State={result.State}; ExitCode={result.ExitCode}";
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { reason = $"命令观察失败；ExceptionType={exception.GetType().Name}"; }
            }
            else if (clock.Elapsed > limit) reason = "等待超时";
            if (reason is not null)
            {
                var recent = string.Join(Environment.NewLine, output.TakeLast(8).Select(line =>
                {
                    var text = line.Text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
                    return $"{line.Stream}: {(text.Length > 256 ? text[..255] + "…" : text)}";
                }));
                var message = $"合成 Cmd 命令未就绪。阶段：{phase}；{reason}。" + Environment.NewLine +
                    $"Elapsed={clock.Elapsed.TotalSeconds:F3}s; Limit={limit.TotalSeconds:F3}s; Completion={session.Completion.Status}; " +
                    $"OwnershipReleased={session.IsOwnershipReleased}; StopRequested={session.IsStopRequested}" + Environment.NewLine +
                    "最近脱敏输出（最多8条，每条256字符）：" + Environment.NewLine + (recent.Length == 0 ? "（无输出）" : recent);
                TestContext.WriteLine(message);
                Assert.Fail(message);
            }
            await Task.Delay(20);
        }
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TalosDesk-Cmd-Tests-" + Guid.NewGuid().ToString("N"), "中文 & (空格)");
        public Sandbox() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, true);
    }
}
