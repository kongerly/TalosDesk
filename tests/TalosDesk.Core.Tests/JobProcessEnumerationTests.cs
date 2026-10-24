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
