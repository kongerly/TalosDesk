using System.Diagnostics;
using System.IO;
using TalosDesk.Core.Diagnostics;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class CrashProcessIntegrationTests
{
    [TestMethod]
    [DataRow("dispatcher", CrashSource.Dispatcher)]
    [DataRow("background", CrashSource.AppDomain)]
    public async Task RealUnhandledExceptionRecordsThenExitsAndClosesOwnedJob(string mode, CrashSource expectedSource)
    {
        using var sandbox = new TemporaryDirectory();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var pidFile = Path.Combine(sandbox.Path, "target.pid");
        var repository = FindRepositoryRoot();
        var hostPath = Path.Combine(repository, "tests", "TalosDesk.CrashTestHost", "bin", "Release",
            "net10.0-windows", "TalosDesk.CrashTestHost.exe");
        Assert.IsTrue(File.Exists(hostPath), hostPath);
        using var host = Process.Start(new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            ArgumentList = { workspace, mode, pidFile }
        }) ?? throw new AssertFailedException("无法启动崩溃测试宿主。");

        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.AreNotEqual(0, host.ExitCode);
        Assert.IsTrue(File.Exists(pidFile), "宿主未启动归属命令。");
        var targetPid = int.Parse(await File.ReadAllTextAsync(pidFile));
        Assert.IsTrue(SpinWait.SpinUntil(() => HasExited(targetPid), TimeSpan.FromSeconds(10)),
            "宿主异常退出后，归属 Job 中的目标进程仍未退出。");

        var store = new CrashRecordStore(workspace);
        var records = store.List().Records;
        Assert.HasCount(1, records);
        var record = store.Read(records[0].FileName).Record!;
        Assert.AreEqual(expectedSource, record.Source);
        Assert.IsTrue(record.IsFatal);
        Assert.IsFalse(File.ReadAllText(Path.Combine(store.RootPath, records[0].FileName))
            .Contains("synthetic-secret", StringComparison.Ordinal));
    }

    private static bool HasExited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TalosDesk.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TalosDesk.Crash.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
