using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RunLogStoreTests
{
    [TestMethod]
    public void PersistsStreamsAndSettingsAcrossRestartInIsolatedWorkspace()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var first = new RunLogStore(workspace);
        first.Initialize();
        first.UpdateSettings(new RunLogSettings(2 * 1024 * 1024, 7));
        var writer = first.Begin(projectId, commandId, DateTimeOffset.Now);
        first.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "public output"));
        first.Append(writer, new CommandOutput(DateTimeOffset.Now, "stderr", "diagnostic output"));
        first.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));

        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        var run = reopened.GetRuns(projectId, commandId).Single();
        Assert.AreEqual(2 * 1024 * 1024, reopened.Settings.MaxBytes);
        Assert.AreEqual(7, reopened.Settings.RetentionDays);
        Assert.AreEqual("Succeeded", run.State);
        CollectionAssert.AreEqual(new[] { "public output" }, reopened.ReadTail(run, "stdout").ToArray());
        CollectionAssert.AreEqual(new[] { "diagnostic output" }, reopened.ReadTail(run, "stderr").ToArray());
        Assert.IsTrue(reopened.RootPath.StartsWith(sandbox.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RotatesOldCompletedRunsBeforeTruncatingAnActiveRun()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var old = store.Begin(projectId, commandId, DateTimeOffset.Now.AddMinutes(-2));
        store.Append(old, new CommandOutput(DateTimeOffset.Now, "stdout", new string('a', 700_000)));
        store.Complete(old, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var current = store.Begin(projectId, commandId, DateTimeOffset.Now);
        store.Append(current, new CommandOutput(DateTimeOffset.Now, "stderr", new string('b', 700_000)));
        Assert.IsFalse(current.Info.Truncated);
        Assert.HasCount(1, store.GetRuns(projectId, commandId));
        store.Append(current, new CommandOutput(DateTimeOffset.Now, "stdout", new string('c', 700_000)));
        Assert.IsTrue(current.Info.Truncated);
        store.Complete(current, new CommandRunResult(CommandRunState.Failed, 1, false));
        var remaining = store.GetRuns(projectId, commandId).Single();
        Assert.IsTrue(remaining.Truncated);
        Assert.AreEqual("Failed", remaining.State);
    }

    [TestMethod]
    public void ExpiresOldRunsAndPreservesActiveRunsDuringClear()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var old = store.Begin(projectId, commandId, DateTimeOffset.Now.AddDays(-35));
        store.Complete(old, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        Assert.HasCount(0, store.GetRuns(projectId, commandId));
        var active = store.Begin(projectId, commandId, DateTimeOffset.Now);
        store.ClearHistory();
        Assert.HasCount(1, store.GetRuns(projectId, commandId));
        store.Complete(active, new CommandRunResult(CommandRunState.Stopped, 1, false));
        store.ClearHistory();
        Assert.HasCount(0, store.GetRuns(projectId, commandId));
    }

    [TestMethod]
    public void RecoversUnfinishedRunAsInterrupted()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var active = store.Begin(projectId, commandId, DateTimeOffset.Now);
        store.Append(active, new CommandOutput(DateTimeOffset.Now, "stdout", "before interruption"));
        active.Stdout.Dispose();
        active.Stderr.Dispose();
        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        var run = reopened.GetRuns(projectId, commandId).Single();
        Assert.AreEqual("Interrupted", run.State);
        Assert.IsNotNull(run.EndedAt);
        CollectionAssert.AreEqual(new[] { "before interruption" }, reopened.ReadTail(run, "stdout").ToArray());
    }

    [TestMethod]
    public void UnwritableLogRootCanBeHandledWithoutCreatingAWorkspaceFile()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        File.WriteAllText(workspace + ".logs", "block directory creation");
        var store = new RunLogStore(workspace);
        Assert.ThrowsExactly<IOException>(() => store.Initialize());
        Assert.IsFalse(File.Exists(workspace));
    }

    [TestMethod]
    public void WriteFailureMarksBatchIncomplete()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var writer = store.Begin(projectId, commandId, DateTimeOffset.Now);
        writer.Stdout.Dispose();
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "must not throw"));
        Assert.IsTrue(writer.Info.WriteFailed);
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        Assert.IsTrue(reopened.GetRuns(projectId, commandId).Single().WriteFailed);
    }

    [TestMethod]
    public async Task SyntheticConcurrentCommandsPersistAllOutputBeyondMemoryLimit()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "isolated-workspace.json"));
        store.Initialize();
        var runner = new CommandRunner();
        var projectId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstWriter = store.Begin(projectId, firstId, DateTimeOffset.Now);
        var secondWriter = store.Begin(projectId, secondId, DateTimeOffset.Now);
        await using var first = runner.Start(firstId, "1..10005 | ForEach-Object { Write-Output \"line-$_\" }", sandbox.Path,
            (_, line) => store.Append(firstWriter, line));
        await using var second = runner.Start(secondId, "Write-Error 'diagnostic'; exit 3", sandbox.Path,
            (_, line) => store.Append(secondWriter, line));
        var results = await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(40));
        store.Complete(firstWriter, results[0]);
        store.Complete(secondWriter, results[1]);

        var firstRun = store.GetRuns(projectId, firstId).Single();
        var secondRun = store.GetRuns(projectId, secondId).Single();
        Assert.AreEqual(CommandRunState.Succeeded, results[0].State);
        Assert.AreEqual(CommandRunState.Failed, results[1].State);
        Assert.HasCount(10_000, first.GetRecentOutput());
        Assert.AreEqual(10_005, File.ReadLines(Path.Combine(store.RootPath,
            projectId.ToString("N"), firstId.ToString("N"), firstRun.RunId.ToString("N"), "stdout.log")).Count());
        Assert.IsTrue(store.ReadTail(secondRun, "stderr").Any(line => line.Contains("diagnostic", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task StoppedCommandKeepsOutputAndStoppedResult()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var writer = store.Begin(projectId, commandId, DateTimeOffset.Now);
        var runner = new CommandRunner();
        await using var session = runner.Start(commandId,
            "Write-Output 'started-marker'; while ($true) { Start-Sleep -Milliseconds 100 }", sandbox.Path,
            (_, line) => store.Append(writer, line));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!session.GetRecentOutput().Any(line => line.Text == "started-marker") && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.IsTrue(session.GetRecentOutput().Any(line => line.Text == "started-marker"));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(12));
        store.Complete(writer, result);
        var run = store.GetRuns(projectId, commandId).Single();
        Assert.AreEqual("Stopped", run.State);
        Assert.IsTrue(store.ReadTail(run, "stdout").Contains("started-marker"));
    }

    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-log-tests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => System.IO.Directory.Delete(Path, true);
    }
}
