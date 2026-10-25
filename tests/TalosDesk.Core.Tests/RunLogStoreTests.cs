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
    [DataRow(5)]
    [DataRow(32)]
    public void ChangingLocationRetriesTransientRenameWithoutRecopying(int error)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var attempts = 0;
        string? firstStaging = null;
        var store = new RunLogStore(workspace, File.OpenRead, (source, target) =>
        {
            firstStaging ??= source;
            Assert.AreEqual(firstStaging, source);
            if (++attempts <= 2) throw new IOException("合成暂时占用", unchecked((int)0x80070000) | error);
            Directory.Move(source, target);
        });
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(2 * 1024 * 1024, 7));
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "中文迁移 marker"));
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var original = store.RootPath;

        Assert.IsNull(store.ChangeLocation(Path.Combine(sandbox.Path, "selected")));
        Assert.AreEqual(3, attempts);
        Assert.IsFalse(Directory.Exists(original));
        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        Assert.AreEqual(store.RootPath, reopened.RootPath);
        Assert.AreEqual(7, reopened.Settings.RetentionDays);
        CollectionAssert.AreEqual(new[] { "中文迁移 marker" }, reopened.ReadTail(writer.Info, "stdout").ToArray());
    }

    [TestMethod]
    [DataRow(5, 6)]
    [DataRow(32, 6)]
    [DataRow(3, 1)]
    public void ChangingLocationPermanentRenameFailureKeepsOriginalAndCleansStaging(int error, int expectedAttempts)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var attempts = 0;
        var store = new RunLogStore(workspace, File.OpenRead, (_, _) =>
        {
            attempts++;
            throw new IOException("合成持续失败", unchecked((int)0x80070000) | error);
        });
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "preserved"));
        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var original = store.RootPath;
        var selected = Path.Combine(sandbox.Path, "selected");

        Assert.ThrowsExactly<IOException>(() => store.ChangeLocation(selected));
        Assert.AreEqual(expectedAttempts, attempts);
        Assert.AreEqual(original, store.RootPath);
        Assert.IsFalse(File.Exists(workspace + ".log-location.json"));
        Assert.HasCount(0, Directory.GetFileSystemEntries(selected));
        CollectionAssert.AreEqual(new[] { "preserved" }, store.ReadTail(writer.Info, "stdout").ToArray());
        var reopened = new RunLogStore(workspace);
        reopened.Initialize();
        Assert.AreEqual(original, reopened.RootPath);
    }

    [TestMethod]
    public void ChangingLocationDoesNotRetryOrRemoveDestinationCreatedDuringRename()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var attempts = 0;
        var store = new RunLogStore(workspace, File.OpenRead, (_, target) =>
        {
            attempts++;
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "unrelated");
            throw new IOException("合成目标竞争", unchecked((int)0x80070005));
        });
        store.Initialize();
        var original = store.RootPath;
        var selected = Path.Combine(sandbox.Path, "selected");
        Assert.ThrowsExactly<IOException>(() => store.ChangeLocation(selected));
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(original, store.RootPath);
        var target = Directory.GetDirectories(selected).Single();
        Assert.AreEqual("unrelated", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.IsFalse(File.Exists(workspace + ".log-location.json"));
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
    [DataRow(false)]
    [DataRow(true)]
    public void ChangingLocationRejectsAliasedDestinationInsideSourceBeforeCopy(bool nestedParent)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        var source = store.RootPath;
        var parent = nestedParent ? Directory.CreateDirectory(Path.Combine(source, "nested")).FullName : source;
        var alias = Path.Combine(sandbox.Path, "alias with spaces");
        CreateJunction(alias, parent);
        try
        {
            var blocker = Path.Combine(source, "copy-safety-blocker.txt");
            File.WriteAllText(blocker, "synthetic source must remain unchanged");
            // 若包含检查回归，拒读句柄会在目录递归前中止复制，避免测试自身重复复制源目录。
            using (var locked = new FileStream(blocker, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failure = Assert.ThrowsExactly<IOException>(() => store.ChangeLocation(alias));
                StringAssert.Contains(failure.Message, "不能互相包含");
            }
            Assert.AreEqual(source, store.RootPath);
            Assert.AreEqual("synthetic source must remain unchanged", File.ReadAllText(blocker));
            Assert.IsFalse(File.Exists(workspace + ".log-location.json"));
            Assert.IsEmpty(Directory.GetDirectories(source, "*.moving-*", SearchOption.AllDirectories));
            Assert.HasCount(nestedParent ? 1 : 0, Directory.GetDirectories(source));
        }
        finally { Directory.Delete(alias); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChangingLocationToSamePhysicalRootThroughJunctionIsNoOp(bool sourceUsesAlias)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var parent = Directory.CreateDirectory(Path.Combine(sandbox.Path, "selected")).FullName;
        var alias = Path.Combine(sandbox.Path, "alias with spaces");
        CreateJunction(alias, parent);
        try
        {
            var store = new RunLogStore(workspace);
            store.Initialize();
            var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
            store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "synthetic preserved history"));
            store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
            store.ChangeLocation(sourceUsesAlias ? alias : parent);
            var originalRoot = store.RootPath;
            var location = File.ReadAllBytes(workspace + ".log-location.json");

            Assert.IsNull(store.ChangeLocation(sourceUsesAlias ? parent : alias));

            Assert.AreEqual(originalRoot, store.RootPath);
            CollectionAssert.AreEqual(location, File.ReadAllBytes(workspace + ".log-location.json"));
            var reopened = new RunLogStore(workspace);
            reopened.Initialize();
            Assert.AreEqual(originalRoot, reopened.RootPath);
            Assert.HasCount(1, reopened.GetRuns(writer.Info.ProjectId, writer.Info.CommandId));
            CollectionAssert.AreEqual(new[] { "synthetic preserved history" }, reopened.ReadTail(writer.Info, "stdout").ToArray());
            Assert.IsEmpty(Directory.GetDirectories(parent, "*.moving-*"));
        }
        finally { Directory.Delete(alias); }
    }

    [TestMethod]
    public void ChangingLocationRejectsAliasedDestinationContainingSource()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        store.ChangeLocation(Path.Combine(sandbox.Path, "seed"));
        var parent = Path.Combine(sandbox.Path, "selected");
        var ancestor = Path.Combine(parent, Path.GetFileName(store.RootPath));
        store.ChangeLocation(Path.Combine(ancestor, "nested"));
        var source = store.RootPath;
        var location = File.ReadAllBytes(workspace + ".log-location.json");
        var alias = Path.Combine(sandbox.Path, "alias with spaces");
        CreateJunction(alias, parent);
        try
        {
            var failure = Assert.ThrowsExactly<IOException>(() => store.ChangeLocation(alias));
            StringAssert.Contains(failure.Message, "不能互相包含");
            Assert.AreEqual(source, store.RootPath);
            CollectionAssert.AreEqual(location, File.ReadAllBytes(workspace + ".log-location.json"));
            Assert.IsTrue(Directory.Exists(source));
            Assert.IsEmpty(Directory.GetDirectories(parent, "*.moving-*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(alias); }
    }

    private static void CreateJunction(string link, string target)
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var process = Process.Start(new ProcessStartInfo(cmd, $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
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
    [DataRow("clear")]
    [DataRow("retention")]
    [DataRow("begin")]
    public void FailedHistoryCleanupRecountsDeletedBytesBeforeActiveOutputContinues(string cleanup)
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var first = store.Begin(projectId, commandId, DateTimeOffset.Now.AddDays(-2).AddMinutes(-1));
        store.Append(first, new CommandOutput(DateTimeOffset.Now, "stdout", new string('a', 400_000)));
        store.Complete(first, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var second = store.Begin(projectId, commandId, DateTimeOffset.Now.AddDays(-2));
        store.Append(second, new CommandOutput(DateTimeOffset.Now, "stdout", new string('b', 400_000)));
        store.Complete(second, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var active = store.Begin(projectId, commandId, DateTimeOffset.Now);
        var history = cleanup == "clear"
            ? Directory.GetDirectories(Path.GetDirectoryName(first.Directory)!).Where(path => path != active.Directory).ToArray()
            : new[] { first.Directory, second.Directory };
        using var lockedMetadata = new FileStream(Path.Combine(history[1], "run.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Action cleanupAction = cleanup switch
            {
                "clear" => store.ClearHistory,
                "retention" => () => store.UpdateSettings(new RunLogSettings(1024 * 1024, 1)),
                _ => () => store.Begin(projectId, commandId, DateTimeOffset.Now.AddDays(40))
            };
            Assert.ThrowsExactly<IOException>(cleanupAction);
            Assert.IsFalse(Directory.Exists(history[0]), "清理失败前应已经删除了一个历史批次。");
            Assert.IsTrue(File.Exists(Path.Combine(history[1], "run.json")), "文件锁必须保留失败的清理对象。");
            var output = new string('c', 800_000);
            var outputBytes = Encoding.UTF8.GetByteCount(output + Environment.NewLine);
            TestContext.WriteLine($"清理失败后实际批次字节：{GetBatchBytes(store)}；待写入：{outputBytes}。");
            Assert.IsLessThanOrEqualTo(store.Settings.MaxBytes, GetBatchBytes(store) + outputBytes, "实际剩余文件与新输出应能放入容量上限。");

            store.Append(active, new CommandOutput(DateTimeOffset.Now, "stdout", output));

            Assert.IsFalse(active.Info.WriteFailed, "清理失败不能让已删除的字节阻止后续正常写入。");
            Assert.IsFalse(active.Info.Truncated);
            Assert.AreEqual((long)outputBytes, new FileInfo(Path.Combine(active.Directory, "stdout.log")).Length);
            CollectionAssert.AreEqual(new[] { output }, store.ReadTail(active.Info, "stdout").ToArray());
        }
        finally
        {
            lockedMetadata.Dispose();
            store.Complete(active, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        }
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
    [DataRow(CommandRunState.Succeeded, 0, false, false)]
    [DataRow(CommandRunState.Failed, 7, false, false)]
    [DataRow(CommandRunState.Stopped, -1, false, false)]
    [DataRow(null, null, false, false)]
    [DataRow(CommandRunState.Failed, 7, true, false)]
    [DataRow(CommandRunState.Succeeded, 0, false, true)]
    public void CompletingBatchRecreatesMissingMetadataAndPreservesResultAndIntegrity(CommandRunState? state, int? exitCode, bool writeFailure, bool truncated)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var projectId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var writer = store.Begin(projectId, commandId, DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "synthetic-preserved-output"));
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stderr", "synthetic-preserved-diagnostic"));
        File.Delete(Path.Combine(writer.Directory, "run.json"));
        if (writeFailure)
        {
            writer.Stdout.Dispose();
            store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "cannot-write"));
            Assert.IsTrue(writer.Info.WriteFailed);
        }
        if (truncated)
        {
            store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", new string('x', 1_100_000)));
            Assert.IsTrue(writer.Info.Truncated);
        }

        store.Complete(writer, state is { } completed ? new CommandRunResult(completed, exitCode!.Value, state == CommandRunState.Stopped) : null);

        Assert.IsTrue(File.Exists(Path.Combine(writer.Directory, "run.json")), "收尾应重建本次持有批次的缺失元数据。");
        Assert.AreEqual(writeFailure, writer.Info.WriteFailed);
        Assert.AreEqual(truncated, writer.Info.Truncated);
        foreach (var current in new[] { store, new RunLogStore(workspace) })
        {
            if (!ReferenceEquals(current, store)) current.Initialize();
            var saved = current.GetRuns(projectId, commandId).Single();
            Assert.AreEqual(writer.Info.RunId, saved.RunId);
            Assert.AreEqual(writer.Info.StartedAt, saved.StartedAt);
            Assert.AreEqual(state?.ToString() ?? "Interrupted", saved.State);
            Assert.AreEqual(exitCode, saved.ExitCode);
            Assert.IsNotNull(saved.EndedAt);
            Assert.AreEqual(writeFailure, saved.WriteFailed);
            Assert.AreEqual(truncated, saved.Truncated);
            CollectionAssert.AreEqual(new[] { "synthetic-preserved-output" }, current.ReadTail(saved, "stdout").ToArray());
            CollectionAssert.AreEqual(new[] { "synthetic-preserved-diagnostic" }, current.ReadTail(saved, "stderr").ToArray());
        }
    }

    [TestMethod]
    public void CompletingBatchDoesNotRecreateMissingRunDirectory()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "synthetic-removed-output"));
        writer.Stdout.Dispose();
        writer.Stderr.Dispose();
        Directory.Delete(writer.Directory, true);

        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));

        Assert.IsTrue(writer.Info.WriteFailed);
        Assert.IsFalse(Directory.Exists(writer.Directory));
        Assert.HasCount(0, store.GetRuns(writer.Info.ProjectId, writer.Info.CommandId));
    }

    [TestMethod]
    [DataRow(true, false, CommandRunState.Succeeded, 0, false)]
    [DataRow(false, true, CommandRunState.Failed, 7, true)]
    [DataRow(true, true, CommandRunState.Stopped, -1, false)]
    [DataRow(true, true, null, null, true)]
    public void ClosingOutputFailureStillFinalizesBothStreamsAndReleasesBatch(bool failStdout, bool failStderr,
        CommandRunState? state, int? exitCode, bool accessDenied)
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new RunLogStore(workspace);
        store.Initialize();
        var original = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(original, new CommandOutput(DateTimeOffset.Now, "stdout", "synthetic-close-output"));
        store.Append(original, new CommandOutput(DateTimeOffset.Now, "stderr", "synthetic-close-diagnostic"));
        original.Stdout.Dispose();
        original.Stderr.Dispose();
        var stdout = new FailingCloseStream(Path.Combine(original.Directory, "stdout.log"), failStdout, accessDenied);
        var stderr = new FailingCloseStream(Path.Combine(original.Directory, "stderr.log"), failStderr, accessDenied);
        var writer = new RunLogWriter(original.Info, original.Directory, stdout, stderr);
        try
        {
            Exception? closeError = null;
            try { store.Complete(writer, state is { } completed ? new CommandRunResult(completed, exitCode!.Value, false) : null); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { closeError = exception; }

            // 与桌面一样，即使捕获了收尾IO错误，也不能留下永远阻止迁移的活动批次。
            Assert.IsNull(store.ChangeLocation(Path.Combine(sandbox.Path, "relocated")));
            Assert.IsNull(closeError, "预期的流关闭错误应通过完整性标记报告，继续执行其余收尾。");
            Assert.AreEqual(1, stdout.CloseAttempts);
            Assert.AreEqual(1, stderr.CloseAttempts);
            Assert.IsFalse(stdout.CanWrite);
            Assert.IsFalse(stderr.CanWrite);
            foreach (var current in new[] { store, new RunLogStore(workspace) })
            {
                if (!ReferenceEquals(current, store)) current.Initialize();
                var saved = current.GetRuns(writer.Info.ProjectId, writer.Info.CommandId).Single();
                Assert.AreEqual(state?.ToString() ?? "Interrupted", saved.State);
                Assert.AreEqual(exitCode, saved.ExitCode);
                Assert.IsNotNull(saved.EndedAt);
                Assert.IsTrue(saved.WriteFailed);
                CollectionAssert.AreEqual(new[] { "synthetic-close-output" }, current.ReadTail(saved, "stdout").ToArray());
                CollectionAssert.AreEqual(new[] { "synthetic-close-diagnostic" }, current.ReadTail(saved, "stderr").ToArray());
            }
            store.Complete(writer, new CommandRunResult(CommandRunState.Failed, 99, false));
            Assert.AreEqual(1, stdout.CloseAttempts, "重复收尾不能再次关闭或覆盖原结果。");
            Assert.AreEqual(1, stderr.CloseAttempts);
            Assert.AreEqual(state?.ToString() ?? "Interrupted", writer.Info.State);
            Assert.AreEqual(exitCode, writer.Info.ExitCode);
            store.ClearHistory();
            Assert.HasCount(0, store.GetRuns(writer.Info.ProjectId, writer.Info.CommandId));
        }
        finally
        {
            stdout.FailOnClose = false;
            stderr.FailOnClose = false;
            stdout.Dispose();
            stderr.Dispose();
        }
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, false)]
    public void DiscardCloseFailureStillClosesBothStreamsDeletesBatchAndReleasesMigration(bool failStdout, bool failStderr, bool accessDenied)
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        var original = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        original.Stdout.Dispose();
        original.Stderr.Dispose();
        var stdout = new FailingCloseStream(Path.Combine(original.Directory, "stdout.log"), failStdout, accessDenied);
        var stderr = new FailingCloseStream(Path.Combine(original.Directory, "stderr.log"), failStderr, accessDenied);
        var writer = new RunLogWriter(original.Info, original.Directory, stdout, stderr);
        try
        {
            var failure = accessDenied
                ? (Exception)Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.Discard(writer))
                : Assert.ThrowsExactly<IOException>(() => store.Discard(writer));
            Assert.AreEqual("synthetic-close-failure", failure.Message);
            Assert.AreSame(failStdout ? stdout.LastCloseError : stderr.LastCloseError, failure, "继续清理后仍须报告首个原始错误。");
            Assert.AreEqual(1, stdout.CloseAttempts);
            Assert.AreEqual(1, stderr.CloseAttempts);
            Assert.IsFalse(stdout.CanWrite);
            Assert.IsFalse(stderr.CanWrite);
            Assert.IsTrue(writer.Info.WriteFailed);
            Assert.IsFalse(Directory.Exists(writer.Directory));
            Assert.IsEmpty(store.GetRuns(writer.Info.ProjectId, writer.Info.CommandId));
            Assert.IsNull(store.ChangeLocation(Path.Combine(sandbox.Path, "relocated")));
            store.Discard(writer);
            store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));
            Assert.AreEqual(1, stdout.CloseAttempts);
            Assert.AreEqual(1, stderr.CloseAttempts);
            Assert.AreEqual(0L, GetBatchBytes(store));
        }
        finally
        {
            stdout.FailOnClose = false;
            stderr.FailOnClose = false;
            stdout.Dispose();
            stderr.Dispose();
        }
    }

    [TestMethod]
    public void FailedBeginRecountsRolledBackBatchBeforeOtherOutputExactlyFitsQuota()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var project = Guid.NewGuid();
        var command = Guid.NewGuid();
        var history = store.Begin(project, Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-1));
        store.Append(history, new CommandOutput(DateTimeOffset.Now, "stdout", new string('x', 400_000)));
        store.Complete(history, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var active = store.Begin(project, command, DateTimeOffset.Now);
        try
        {
            var newlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
            var initialSpace = checked((int)(store.Settings.MaxBytes - GetBatchBytes(store))) - newlineBytes;
            store.Append(active, new CommandOutput(DateTimeOffset.Now, "stdout", new string('a', initialSpace)));
            Assert.AreEqual(store.Settings.MaxBytes, GetBatchBytes(store));
            using (var locked = new FileStream(Path.Combine(history.Directory, "run.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // 首次 Prune 无需清理；创建新元数据后才触及锁住的历史，触发实际创建回滚。
                Assert.ThrowsExactly<IOException>(() => store.Begin(project, command, DateTimeOffset.Now));
                Assert.HasCount(1, store.GetRuns(project, command), "失败创建不能留下虚假批次或活动保护。");
                Assert.HasCount(1, Directory.GetDirectories(Path.GetDirectoryName(active.Directory)!));
                var available = checked((int)(store.Settings.MaxBytes - GetBatchBytes(store))) - newlineBytes;
                Assert.IsGreaterThan(100_000, available, "已删除的历史输出应腾出真实空间。");

                store.Append(active, new CommandOutput(DateTimeOffset.Now, "stdout", new string('b', available)));

                Assert.IsFalse(active.Info.WriteFailed, "已经回滚并删除的新批次不能继续计入容量，触发对锁定历史的错误清理。");
                Assert.IsFalse(active.Info.Truncated);
                Assert.AreEqual(store.Settings.MaxBytes, GetBatchBytes(store));
                Assert.AreEqual(available, store.ReadTail(active.Info, "stdout", 1).Single().Length);
            }
        }
        finally { store.Complete(active, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }
        Assert.IsNull(store.ChangeLocation(Path.Combine(sandbox.Path, "relocated")));
    }

    [TestMethod]
    public void FailedDiscardKeepsRemainingBytesInQuotaAndLeavesBatchCleanable()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var discarded = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(discarded, new CommandOutput(DateTimeOffset.Now, "stdout", new string('x', 900_000)));
        using (var locked = new FileStream(Path.Combine(discarded.Directory, "stdout.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsExactly<IOException>(() => store.Discard(discarded));
            Assert.IsFalse(discarded.Stdout.CanWrite);
            Assert.IsFalse(discarded.Stderr.CanWrite);
            var next = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
            try
            {
                store.Append(next, new CommandOutput(DateTimeOffset.Now, "stdout", new string('y', 500_000)));
                Assert.IsTrue(next.Info.WriteFailed || next.Info.Truncated, "不能把未能删除的批次字节提前从容量中扣除。");
                Assert.IsLessThanOrEqualTo(store.Settings.MaxBytes, GetBatchBytes(store));
                Assert.IsTrue(discarded.Info.WriteFailed);
            }
            finally { store.Complete(next, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }
        }
        store.ClearHistory();
        Assert.AreEqual(0L, GetBatchBytes(store));
        Assert.IsNull(store.ChangeLocation(Path.Combine(sandbox.Path, "relocated")));
    }

    [TestMethod]
    public void DiscardMissingBatchRecountsQuotaAndAllowsNextRun()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", new string('x', 900_000)));
        writer.Stdout.Dispose();
        writer.Stderr.Dispose();
        Directory.Delete(writer.Directory, true);

        store.Discard(writer);

        Assert.IsFalse(writer.Info.WriteFailed);
        var next = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(next, new CommandOutput(DateTimeOffset.Now, "stdout", new string('y', 500_000)));
        store.Complete(next, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        Assert.IsFalse(next.Info.WriteFailed);
        Assert.IsFalse(next.Info.Truncated);
        Assert.AreEqual(500_000, store.ReadTail(next.Info, "stdout").Single().Length);
        Assert.IsNull(store.ChangeLocation(Path.Combine(sandbox.Path, "relocated")));
    }

    [TestMethod]
    public void RecreatedMetadataDoesNotDoubleCountMissingBytesWhenOtherOutputExactlyFitsQuota()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        store.UpdateSettings(new RunLogSettings(1024 * 1024, 30));
        var history = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(history, new CommandOutput(DateTimeOffset.Now, "stdout", "synthetic-history"));
        File.Delete(Path.Combine(history.Directory, "run.json"));
        store.Complete(history, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var active = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        try
        {
            var remainingBytes = store.Settings.MaxBytes - GetBatchBytes(store);
            var output = new string('x', checked((int)remainingBytes - Encoding.UTF8.GetByteCount(Environment.NewLine)));

            store.Append(active, new CommandOutput(DateTimeOffset.Now, "stdout", output));

            Assert.IsFalse(active.Info.WriteFailed);
            Assert.IsFalse(active.Info.Truncated);
            Assert.IsTrue(Directory.Exists(history.Directory), "真实容量足够时不能因重复计入已丢失元数据而删除历史。");
            Assert.AreEqual(store.Settings.MaxBytes, GetBatchBytes(store));
            CollectionAssert.AreEqual(new[] { "synthetic-history" }, store.ReadTail(history.Info, "stdout").ToArray());
        }
        finally { store.Complete(active, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }
    }

    [TestMethod]
    public void MetadataReplacementFailureIsReportedThroughWriterWithoutChangingSavedOutput()
    {
        using var sandbox = new Sandbox();
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stdout", "synthetic-final-output"));
        store.Append(writer, new CommandOutput(DateTimeOffset.Now, "stderr", "synthetic-final-diagnostic"));
        var metadata = Path.Combine(writer.Directory, "run.json");
        var original = File.ReadAllBytes(metadata);
        using var lockedMetadata = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read);

        store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false));

        Assert.IsTrue(writer.Info.WriteFailed, "调用方必须检查失败标记，收尾IO错误不会从Complete抛出。");
        Assert.AreEqual("Succeeded", writer.Info.State);
        Assert.AreEqual(0, writer.Info.ExitCode);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(metadata));
        Assert.IsFalse(File.Exists(metadata + ".tmp"));
        CollectionAssert.AreEqual(new[] { "synthetic-final-output" }, store.ReadTail(writer.Info, "stdout").ToArray());
        CollectionAssert.AreEqual(new[] { "synthetic-final-diagnostic" }, store.ReadTail(writer.Info, "stderr").ToArray());
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

    private sealed class FailingCloseStream(string path, bool failOnClose, bool accessDenied)
        : FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)
    {
        internal bool FailOnClose { get; set; } = failOnClose;
        internal int CloseAttempts { get; private set; }
        internal Exception? LastCloseError { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseAttempts++;
            base.Dispose(disposing);
            if (!disposing || !FailOnClose) return;
            LastCloseError = accessDenied ? new UnauthorizedAccessException("synthetic-close-failure") : new IOException("synthetic-close-failure");
            throw LastCloseError;
        }
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
