using System.ComponentModel;
using System.Globalization;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class JobProcessEnumerationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1)]
    [DataRow(128)]
    public async Task CompleteJobListIncludesOwnedChildAndKeepsIndependentJobAlive(int initialCapacity)
    {
        using var sandbox = new Sandbox();
        const string command = """
            $info = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.ArgumentList.Add('-NoProfile')
            $info.ArgumentList.Add('-NonInteractive')
            $info.ArgumentList.Add('-Command')
            $info.ArgumentList.Add('while ($true) { Start-Sleep -Milliseconds 100 }')
            $child = [Diagnostics.Process]::Start($info)
            [Console]::WriteLine($child.Id)
            while ($true) { Start-Sleep -Milliseconds 100 }
            """;
        await using var owned = new OwnedCommand(command, sandbox.Path);
        await using var independent = new OwnedCommand("while ($true) { Start-Sleep -Milliseconds 100 }", sandbox.Path);
        var childLine = await owned.Stdout.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsNotNull(childLine);
        var childId = int.Parse(childLine, CultureInfo.InvariantCulture);

        IReadOnlyList<int> ids;
        try { ids = WindowsNative.GetActiveProcessIds(owned.Job, initialCapacity); }
        catch (Win32Exception exception)
        {
            TestContext.WriteLine($"InitialCapacity={initialCapacity}; NativeErrorCode={exception.NativeErrorCode}");
            throw;
        }

        Assert.Contains(owned.ProcessId, ids);
        Assert.Contains(childId, ids);
        Assert.DoesNotContain(independent.ProcessId, ids);
        Assert.AreEqual(ids.Count, ids.Distinct().Count());
        await owned.StopAsync();
        Assert.HasCount(0, WindowsNative.GetActiveProcessIds(owned.Job, initialCapacity));
        Assert.IsFalse(await WindowsNative.WaitForProcessAsync(independent.Process, TimeSpan.Zero, CancellationToken.None),
            "枚举和停止只能影响测试持有的目标Job，独立Job必须仍存活。");
    }

    [TestMethod]
    public async Task DefaultCapacityIncludesEveryChildBeyond128AndKeepsIndependentJobAlive()
    {
        using var sandbox = new Sandbox();
        const string command = """
            $ErrorActionPreference = 'Stop'
            $children = [Collections.Generic.List[Diagnostics.Process]]::new()
            for ($index = 0; $index -lt 129; $index++) {
                $info = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) 'cmd.exe'))
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.RedirectStandardInput = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $info.ArgumentList.Add('/d')
                $info.ArgumentList.Add('/q')
                $info.ArgumentList.Add('/c')
                $info.ArgumentList.Add('set /p TALOSDESK_SYNTHETIC_WAIT=')
                $children.Add([Diagnostics.Process]::Start($info))
            }
            [Console]::WriteLine(($children | ForEach-Object { $_.Id }) -join ',')
            while ($true) { Start-Sleep -Milliseconds 100 }
            """;
        await using var owned = new OwnedCommand(command, sandbox.Path);
        await using var independent = new OwnedCommand("while ($true) { Start-Sleep -Milliseconds 100 }", sandbox.Path);
        var childLine = await owned.Stdout.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsNotNull(childLine);
        var childIds = childLine.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Assert.HasCount(129, childIds);
        Assert.AreEqual(childIds.Length, childIds.Distinct().Count());

        var ids = WindowsNative.GetActiveProcessIds(owned.Job);
        TestContext.WriteLine($"ChildCount={childIds.Length}; JobProcessCount={ids.Count}; InitialCapacity=128");
        Assert.IsGreaterThan(128, ids.Count);
        Assert.Contains(owned.ProcessId, ids);
        foreach (var childId in childIds) Assert.Contains(childId, ids);
        Assert.DoesNotContain(independent.ProcessId, ids);
        Assert.AreEqual(ids.Count, ids.Distinct().Count());
        await owned.StopAsync();
        Assert.HasCount(0, WindowsNative.GetActiveProcessIds(owned.Job));
        Assert.IsFalse(await WindowsNative.WaitForProcessAsync(independent.Process, TimeSpan.Zero, CancellationToken.None),
            "超过默认容量时，也只能停止目标Job，独立Job必须仍存活。");
    }

    private sealed class OwnedCommand : IAsyncDisposable
    {
        internal SafeJobHandle Job { get; }
        internal SafeProcessHandle Process { get; }
        internal int ProcessId { get; }
        internal StreamReader Stdout { get; }
        private StreamReader Stderr { get; }

        internal OwnedCommand(string command, string directory) =>
            (Job, Process, ProcessId, Stdout, Stderr) = WindowsNative.StartPowerShell(command, directory);

        internal async Task StopAsync()
        {
            if (WindowsNative.GetActiveProcessCount(Job) != 0) WindowsNative.TerminateJob(Job);
            Assert.IsTrue(await WindowsNative.WaitForCommandGroupExitAsync(Job, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.IsTrue(await WindowsNative.WaitForProcessAsync(Process, TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally
            {
                Stdout.Dispose();
                Stderr.Dispose();
                Process.Dispose();
                Job.Dispose();
            }
        }
    }

    private sealed class Sandbox : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-job-list-{Guid.NewGuid():N}");
        internal Sandbox() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
