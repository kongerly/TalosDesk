using System.Diagnostics;
using System.Text;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RunLogStoreTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("")]
    [DataRow("\n")]
    [DataRow("\r")]
    [DataRow("\r\n")]
    [DataRow("甲😀\r\n乙\n\n丙\r丁")]
    [DataRow("甲😀\r\n乙\n\n丙\r丁\r\n")]
    public void TailMatchesLineReaderForUtf8AndNewlineBoundaries(string content)
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var path = Path.Combine(writer.Directory, "stdout.log");
        foreach (var encoding in new[] { new UTF8Encoding(false), new UTF8Encoding(true) })
        {
            File.WriteAllText(path, content, encoding);
            var all = File.ReadLines(path).ToArray();
            foreach (var limit in new[] { 1, 2, 3, 10_000 })
                CollectionAssert.AreEqual(all.TakeLast(limit).ToArray(), store.ReadTail(writer.Info, "stdout", limit).ToArray());
        }
    }

    [TestMethod]
    public void TailPreservesLongLinesUnicodeAndCrLfAcrossReadBlocks()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var path = Path.Combine(writer.Directory, "stdout.log");
        var longLine = new string('中', 20_000) + "😀";
        // 从 EOF 向前 16 KiB 的块边界恰好落在 CR 与 LF 之间。
        File.WriteAllText(path, "earlier\r\n" + longLine + "\r\n" + new string('x', 16_382) + "\n", new UTF8Encoding(false));
        foreach (var limit in new[] { 1, 2, 3, 4 })
            CollectionAssert.AreEqual(File.ReadLines(path).TakeLast(limit).ToArray(), store.ReadTail(writer.Info, "stdout", limit).ToArray());
        File.WriteAllText(path, "earlier\n\uFEFF字符😀\n", new UTF8Encoding(false));
        CollectionAssert.AreEqual(new[] { "\uFEFF字符😀" }, store.ReadTail(writer.Info, "stdout", 1).ToArray());
    }

    [TestMethod]
    public void LargeTailReadsOnlyTheEndOfTheFile()
    {
        using var sandbox = new Sandbox();
        ObservedReadStream? observed = null;
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"), path => observed = new ObservedReadStream(path));
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var path = Path.Combine(writer.Directory, "stdout.log");
        using (var file = File.OpenWrite(path))
        {
            var block = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("synthetic-history-line\n", 4096)));
            while (file.Length < 256L * 1024 * 1024) file.Write(block);
            file.Write(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 10).Select(index => $"最后😀-{index}")) + "\n"));
        }
        var clock = Stopwatch.StartNew();
        var tail = store.ReadTail(writer.Info, "stdout", 10);
        clock.Stop();
        CollectionAssert.AreEqual(Enumerable.Range(0, 10).Select(index => $"最后😀-{index}").ToArray(), tail.ToArray());
        Assert.IsLessThanOrEqualTo(32_768L, observed!.BytesRead, "读取量应由文件尾部决定，不能扫描整份历史。");
        TestContext.WriteLine($"256 MiB 合成文件最后 10 行：{clock.Elapsed.TotalMilliseconds:F3} ms，读取 {observed.BytesRead} 字节；不以耗时作为通过门槛。");
    }

    [TestMethod]
    public async Task SlowTailDoesNotBlockWritesCompletionCleanupOrMigration()
    {
        using var sandbox = new Sandbox();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"), path => new ObservedReadStream(path, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("合成历史读取未释放。");
        }));
        store.Initialize();
        var history = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(history, new CommandOutput(DateTimeOffset.Now, "stdout", "snapshot-history"));
        store.Complete(history, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var active = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        var read = Task.Run(() => store.ReadTail(history.Info, "stdout"));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                store.Append(active, new CommandOutput(DateTimeOffset.Now, "stderr", "concurrent-write"));
                Assert.IsFalse(active.Info.WriteFailed);
                store.ClearHistory();
                Assert.IsFalse(Directory.Exists(history.Directory));
                store.Complete(active, new CommandRunResult(CommandRunState.Succeeded, 0, false));
                store.ChangeLocation(Path.Combine(sandbox.Path, "moved"));
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            release.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            store.Complete(active, null);
        }
        CollectionAssert.AreEqual(new[] { "snapshot-history" }, read.Result.ToArray());
    }

    [TestMethod]
    public void TailExcludesLaterAppendsAndCanCancelBetweenBlocks()
    {
        using var sandbox = new Sandbox();
        using var cancellation = new CancellationTokenSource();
        var appended = false;
        var cancelOnRead = false;
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"), path => new ObservedReadStream(path, () =>
        {
            if (cancelOnRead) cancellation.Cancel();
            if (appended) return;
            File.AppendAllText(path, "later\n");
            appended = true;
        }));
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "snapshot"));
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        CollectionAssert.AreEqual(new[] { "snapshot" }, store.ReadTail(writer.Info, "stdout").ToArray());
        File.WriteAllText(Path.Combine(writer.Directory, "stdout.log"), new string('x', 100_000));
        cancelOnRead = true;
        Assert.ThrowsExactly<OperationCanceledException>(() => store.ReadTail(writer.Info, "stdout", cancellationToken: cancellation.Token));
        Assert.HasCount(0, store.ReadTail(writer.Info, "stderr"));
        File.Delete(Path.Combine(writer.Directory, "stdout.log"));
        Assert.HasCount(0, store.ReadTail(writer.Info, "stdout"));
        Assert.ThrowsExactly<ArgumentException>(() => store.ReadTail(writer.Info, "other"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.ReadTail(writer.Info, "stdout", 0));
    }

    private sealed class ObservedReadStream(string path, Action? beforeRead = null) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        internal long BytesRead;
        public override int Read(Span<byte> buffer)
        {
            beforeRead?.Invoke();
            var read = _file.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            beforeRead?.Invoke();
            var read = _file.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }

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
        CollectionAssert.AreEqual(new[] { "public output" }, first.ReadTail(writer.Info, "stdout").ToArray());
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
    public void ChangingLocationMovesHistoryAndSettingsAcrossRestartAndCanRestoreDefault()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var selectedParent = Path.Combine(sandbox.Path, "selected-logs");
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var store = new RunLogStore(workspace);
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(2 * 1024 * 1024, 7));
        var writer = store.Begin(projectId, commandId, DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "moved output"));
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var defaultRoot = store.RootPath;

        Assert.IsNull(store.ChangeLocation(selectedParent));
        var customRoot = store.RootPath;
        Assert.IsTrue(customRoot.StartsWith(selectedParent, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Directory.Exists(defaultRoot));
        Assert.IsTrue(File.Exists(workspace + ".log-location.json"));
        Assert.IsFalse(File.Exists(workspace));
        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        Assert.AreEqual(customRoot, reopened.RootPath);
        Assert.AreEqual(7, reopened.Settings.RetentionDays);
        CollectionAssert.AreEqual(new[] { "moved output" }, reopened.ReadTail(reopened.GetRuns(projectId, commandId).Single(), "stdout").ToArray());

        Assert.IsNull(reopened.ChangeLocation(null));
        Assert.AreEqual(defaultRoot, reopened.RootPath);
        Assert.IsFalse(File.Exists(workspace + ".log-location.json"));
        Assert.IsFalse(Directory.Exists(customRoot));
        var defaultReopened = new RunLogStore(workspace);
        defaultReopened.Initialize();
        CollectionAssert.AreEqual(new[] { "moved output" }, defaultReopened.ReadTail(defaultReopened.GetRuns(projectId, commandId).Single(), "stdout").ToArray());
    }

    [TestMethod]
    public void ChangingLocationRejectsActiveRunsAndExistingDestination()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        var original = store.RootPath;
        var selectedParent = Path.Combine(sandbox.Path, "selected-logs");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.ChangeLocation(selectedParent));
        Assert.AreEqual(original, store.RootPath);
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));

        store.ChangeLocation(selectedParent);
        var occupiedTarget = store.RootPath;
        store.ChangeLocation(null);
        Directory.CreateDirectory(occupiedTarget);
        File.WriteAllText(Path.Combine(occupiedTarget, "keep.txt"), "unrelated");
        Assert.ThrowsExactly<IOException>(() => store.ChangeLocation(selectedParent));
        Assert.AreEqual(original, store.RootPath);
        Assert.AreEqual("unrelated", File.ReadAllText(Path.Combine(occupiedTarget, "keep.txt")));
        Assert.HasCount(1, store.GetRuns(writer.Info.ProjectId, writer.Info.CommandId));
    }

    [TestMethod]
    public void TwoWorkspacesUsingOneSelectedFolderKeepSeparateLogs()
    {
        using var sandbox = new Sandbox();
        var selectedParent = Path.Combine(sandbox.Path, "selected-logs");
        var first = new RunLogStore(Path.Combine(sandbox.Path, "first.json"));
        var second = new RunLogStore(Path.Combine(sandbox.Path, "second.json"));
        first.Initialize();
        second.Initialize();
        first.ChangeLocation(selectedParent);
        second.ChangeLocation(selectedParent);
        Assert.AreNotEqual(first.RootPath, second.RootPath);
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var writer = first.Begin(projectId, commandId, DateTimeOffset.Now);
        first.Append(writer, new CommandOutput(DateTimeOffset.Now, "stderr", "first workspace only"));
        first.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        Assert.HasCount(1, first.GetRuns(projectId, commandId));
        Assert.HasCount(0, second.GetRuns(projectId, commandId));
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
    [DataRow("{")]
    [DataRow(null)]
    [DataRow("null")]
    [DataRow("{}")]
    public void RotatesUnreadableHistoryBeforeTruncatingAnActiveRun(string? metadata)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var old = store.Begin(projectId, commandId, DateTimeOffset.Now.AddMinutes(-2));
        store.Append(old, new CommandOutput(DateTimeOffset.Now, "stdout", new string('a', 700_000)));
        store.Complete(old, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        ReplaceMetadata(old, metadata);

        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        Assert.IsTrue(Directory.Exists(old.Directory));
        Assert.HasCount(0, reopened.GetRuns(projectId, commandId));
        var current = reopened.Begin(projectId, commandId, DateTimeOffset.Now);
        try
        {
            reopened.Append(current, new CommandOutput(DateTimeOffset.Now, "stderr", new string('b', 700_000)));
            Assert.IsFalse(Directory.Exists(old.Directory), "损坏历史必须参与容量轮转。");
            Assert.IsFalse(current.Info.Truncated);
            Assert.IsLessThanOrEqualTo(reopened.Settings.MaxBytes, GetBatchBytes(reopened));
            reopened.Append(current, new CommandOutput(DateTimeOffset.Now, "stdout", new string('c', 700_000)));
            Assert.IsTrue(current.Info.Truncated, "只剩运行中批次时必须提示截断。");
            Assert.AreEqual(0L, new FileInfo(Path.Combine(current.Directory, "stdout.log")).Length);
            Assert.IsLessThanOrEqualTo(reopened.Settings.MaxBytes, GetBatchBytes(reopened));
        }
        finally { reopened.Complete(current, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }
        Assert.IsTrue(reopened.GetRuns(projectId, commandId).Single().Truncated);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow(null)]
    [DataRow("null")]
    [DataRow("{}")]
    public void ClearHistoryRemovesUnreadableBatchesAndPreservesActiveDirectories(string? metadata)
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var old = store.Begin(projectId, commandId, DateTimeOffset.Now.AddMinutes(-2));
        store.Append(old, new CommandOutput(DateTimeOffset.Now, "stdout", "damaged history"));
        store.Complete(old, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        ReplaceMetadata(old, metadata);
        var active = store.Begin(projectId, commandId, DateTimeOffset.Now);
        try
        {
            ReplaceMetadata(active, metadata);
            store.ClearHistory();
            Assert.IsFalse(Directory.Exists(old.Directory));
            Assert.IsTrue(Directory.Exists(active.Directory), "运行中批次不依赖元数据解析结果保护。");
            store.Append(active, new CommandOutput(DateTimeOffset.Now, "stdout", "still active"));
            Assert.IsFalse(active.Info.WriteFailed);
        }
        finally { store.Complete(active, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }
        store.ClearHistory();
        Assert.IsFalse(Directory.Exists(active.Directory));
        Assert.IsTrue(File.Exists(Path.Combine(store.RootPath, "settings.json")));
    }

    [TestMethod]
    public void CapacityRecountAfterWriteFailureIncludesUnreadableActiveBatch()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var first = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        var second = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        try
        {
            store.Append(first, new CommandOutput(DateTimeOffset.Now, "stdout", new string('a', 700_000)));
            ReplaceMetadata(first, "{");
            first.Stdout.Dispose();
            store.Append(first, new CommandOutput(DateTimeOffset.Now, "stdout", "write failure"));
            Assert.IsTrue(first.Info.WriteFailed);
            store.Append(second, new CommandOutput(DateTimeOffset.Now, "stderr", new string('b', 400_000)));
            Assert.IsTrue(second.Info.Truncated, "写入失败后的容量重算不能漏掉损坏批次。");
            Assert.AreEqual(0L, new FileInfo(Path.Combine(second.Directory, "stderr.log")).Length);
            Assert.IsTrue(Directory.Exists(first.Directory));
            Assert.IsTrue(Directory.Exists(second.Directory));
            Assert.IsLessThanOrEqualTo(store.Settings.MaxBytes, GetBatchBytes(store));
        }
        finally
        {
            store.Complete(first, new CommandRunResult(CommandRunState.Succeeded, 0, false));
            store.Complete(second, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        }
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow(null)]
    public void UnreadableHistoryUsesDirectoryTimestampForRetention(string? metadata)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        var old = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Complete(old, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        ReplaceMetadata(old, metadata);
        var recent = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Complete(recent, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        ReplaceMetadata(recent, metadata);
        Directory.SetLastWriteTimeUtc(old.Directory, DateTime.UtcNow.AddDays(-35));
        Directory.SetLastWriteTimeUtc(recent.Directory, DateTime.UtcNow.AddDays(-5));

        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        Assert.IsFalse(Directory.Exists(old.Directory));
        Assert.IsTrue(Directory.Exists(recent.Directory));
        var active = reopened.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        try
        {
            ReplaceMetadata(active, metadata);
            Directory.SetLastWriteTimeUtc(active.Directory, DateTime.UtcNow.AddDays(-35));
            reopened.UpdateSettings(new RunLogSettings(1024 * 1024, 1));
            Assert.IsFalse(Directory.Exists(recent.Directory));
            Assert.IsTrue(Directory.Exists(active.Directory));
        }
        finally { reopened.Complete(active, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }
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

    private static void ReplaceMetadata(RunLogWriter writer, string? metadata)
    {
        var path = Path.Combine(writer.Directory, "run.json");
        if (metadata is null) File.Delete(path);
        else File.WriteAllText(path, metadata);
    }

    private static long GetBatchBytes(RunLogStore store) =>
        Directory.EnumerateFiles(store.RootPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "settings.json")
            .Sum(path => new FileInfo(path).Length);

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
