using System.Diagnostics;
using System.Text;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CmdProcessTests
{
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
        await Until(() => session.GetRecentOutput().Any(line => line.Text == "cmd-ready"));
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
        await File.WriteAllTextAsync(Path.Combine(sandbox.Root, "ignore.ps1"), "Add-Type 'using System; using System.Runtime.InteropServices; public static class IgnoreCtrl { [DllImport(\"kernel32.dll\")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add); }'; [IgnoreCtrl]::SetConsoleCtrlHandler([IntPtr]::Zero,$true)|Out-Null; Write-Output \"child:$PID\"; while($true){Start-Sleep -Milliseconds 100}");
        await using var sentinel = new CommandRunner().Start(Guid.NewGuid(), "Write-Output 'sentinel-ready'; while($true){Start-Sleep -Milliseconds 100}", sandbox.Root);
        await Until(() => sentinel.GetRecentOutput().Any(line => line.Text == "sentinel-ready"));
        await using var session = new CommandRunner().Start(Guid.NewGuid(), $"\"{pwsh}\" -NoProfile -File ignore.ps1", sandbox.Root, shell: CommandShell.Cmd);
        await Until(() => session.GetRecentOutput().Any(line => line.Text.StartsWith("child:", StringComparison.Ordinal)));
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

    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) Assert.Fail("合成 Cmd 命令未就绪。");
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
