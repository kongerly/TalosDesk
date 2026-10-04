using System.Text.Json;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Diagnostics;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class CrashRecordStoreTests
{
    [TestMethod]
    public void WritesBoundedRecordWithoutExceptionMessageOrData()
    {
        using var sandbox = new TemporaryDirectory();
        const string secret = "synthetic-secret-8391";
        var exception = Capture(new InvalidOperationException(secret,
            new ArgumentException("inner-" + secret)));
        exception.Data[secret] = "data-" + secret;
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"));

        var result = store.Write(exception, CrashSource.Dispatcher, true, "0.2.0", "Preview");

        Assert.AreEqual(CrashWriteStatus.Written, result.Status);
        var path = Path.Combine(store.RootPath, result.FileName!);
        var text = File.ReadAllText(path);
        Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Message", StringComparison.Ordinal));
        Assert.IsLessThanOrEqualTo(CrashRecordStore.MaximumRecordBytes, new FileInfo(path).Length);
        var read = store.Read(result.FileName!);
        Assert.AreEqual(CrashReadStatus.Available, read.Status);
        Assert.IsTrue(read.Record!.IsFatal);
        Assert.AreEqual(CrashSource.Dispatcher, read.Record.Source);
        Assert.AreEqual("System.InvalidOperationException", read.Record.Exception.Type);
    }

    [TestMethod]
    public void PersistsOccurredTimeAsUtcWithoutDisplayConversion()
    {
        using var sandbox = new TemporaryDirectory();
        var occurredAtUtc = new DateTimeOffset(2026, 10, 1, 9, 44, 46, 996, TimeSpan.Zero);
        var store = new CrashRecordStore(
            Path.Combine(sandbox.Path, "workspace.json"),
            new FixedTimeProvider(occurredAtUtc));

        var result = store.Write(
            new InvalidOperationException(),
            CrashSource.Dispatcher,
            true,
            "0.2.0",
            "Preview");

        Assert.AreEqual(CrashWriteStatus.Written, result.Status);
        var record = store.Read(result.FileName!).Record!;
        Assert.AreEqual(occurredAtUtc, record.OccurredAtUtc);
        Assert.AreEqual(TimeSpan.Zero, record.OccurredAtUtc.Offset);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.RootPath, result.FileName!)));
        Assert.AreEqual(occurredAtUtc, json.RootElement.GetProperty("OccurredAtUtc").GetDateTimeOffset());
    }

    [TestMethod]
    public void TruncatesLargeAggregateAndLongMetadata()
    {
        using var sandbox = new TemporaryDirectory();
        var exception = new AggregateException(Enumerable.Range(0, 12)
            .Select(index => new InvalidOperationException($"secret-{index}")));
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"));

        var result = store.Write(exception, CrashSource.AppDomain, true, new string('v', 500), new string('c', 500));
        var record = store.Read(result.FileName!).Record!;

        Assert.IsTrue(record.Truncated);
        Assert.HasCount(7, record.Exception.InnerExceptions);
        Assert.AreEqual(256, record.ApplicationVersion.Length);
        Assert.AreEqual(256, record.ReleaseChannel.Length);
    }

    [TestMethod]
    public void ListsCorruptAndUnknownRecordsWithoutShowingTheirContent()
    {
        using var sandbox = new TemporaryDirectory();
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"));
        Directory.CreateDirectory(store.RootPath);
        File.WriteAllText(Path.Combine(store.RootPath, "crash-corrupt.json"), "synthetic-secret-broken");
        File.WriteAllText(Path.Combine(store.RootPath, "unrelated.json"), "leave-me");

        var list = store.List();

        Assert.HasCount(1, list.Records);
        Assert.AreEqual(CrashReadStatus.Corrupted, list.Records[0].Status);
        Assert.IsNull(list.Records[0].ExceptionType);
        Assert.IsTrue(File.Exists(Path.Combine(store.RootPath, "unrelated.json")));
    }

    [TestMethod]
    public void PrunesExpiredTemporaryAndOldestOverflowRecords()
    {
        using var sandbox = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"), new FixedTimeProvider(now));
        Directory.CreateDirectory(store.RootPath);
        var expired = Path.Combine(store.RootPath, "crash-expired.json");
        File.WriteAllText(expired, "{}");
        File.SetLastWriteTimeUtc(expired, now.AddDays(-31).UtcDateTime);
        File.WriteAllText(Path.Combine(store.RootPath, "crash-stale.json.tmp"), "temporary");
        for (var index = 0; index < 101; index++)
        {
            var path = Path.Combine(store.RootPath, $"crash-current-{index:D3}.json");
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, now.AddMinutes(index - 200).UtcDateTime);
        }

        var list = store.List();

        Assert.HasCount(100, list.Records);
        Assert.IsFalse(File.Exists(expired));
        Assert.IsFalse(File.Exists(Path.Combine(store.RootPath, "crash-stale.json.tmp")));
        Assert.IsFalse(File.Exists(Path.Combine(store.RootPath, "crash-current-000.json")));
    }

    [TestMethod]
    public void ConcurrentWritesRemainValidAndWorkspaceIsolated()
    {
        using var sandbox = new TemporaryDirectory();
        var first = new CrashRecordStore(Path.Combine(sandbox.Path, "one.json"));
        var second = new CrashRecordStore(Path.Combine(sandbox.Path, "two.json"));

        Parallel.For(0, 20, index =>
        {
            var result = first.Write(Capture(new InvalidOperationException($"secret-{index}")),
                CrashSource.UnobservedTask, false, "0.2.0", "Preview");
            Assert.AreEqual(CrashWriteStatus.Written, result.Status);
        });

        Assert.HasCount(20, first.List().Records);
        Assert.HasCount(0, second.List().Records);
        Assert.IsTrue(first.List().Records.All(item => first.Read(item.FileName).Status == CrashReadStatus.Available));
    }

    [TestMethod]
    public void DisabledAndWriteFailureDoNotCreateFallbackRecord()
    {
        using var sandbox = new TemporaryDirectory();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new CrashRecordStore(workspace);
        var disabled = store.Write(new InvalidOperationException(), CrashSource.Dispatcher, true, "0.2.0", "Preview", false);
        Assert.AreEqual(CrashWriteStatus.Disabled, disabled.Status);
        Assert.IsFalse(Directory.Exists(store.RootPath));

        File.WriteAllText(store.RootPath, "blocks-directory");
        var failed = store.Write(new InvalidOperationException(), CrashSource.Dispatcher, true, "0.2.0", "Preview");
        Assert.AreEqual(CrashWriteStatus.Failed, failed.Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(sandbox.Path, "fallback")));
    }

    [TestMethod]
    public void ClearOnlyDeletesManagedRecords()
    {
        using var sandbox = new TemporaryDirectory();
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Write(new InvalidOperationException(), CrashSource.Dispatcher, true, "0.2.0", "Preview");
        File.WriteAllText(Path.Combine(store.RootPath, "keep.txt"), "keep");

        var result = store.Clear();

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.DeletedCount);
        Assert.IsTrue(File.Exists(Path.Combine(store.RootPath, "keep.txt")));
    }

    [TestMethod]
    public void SettingsDefaultEnabledAndCorruptionDisablesEditing()
    {
        using var sandbox = new TemporaryDirectory();
        var store = new DiagnosticSettingsStore(Path.Combine(sandbox.Path, "workspace.json"));
        var initial = store.Load();
        Assert.IsTrue(initial.Settings.IsEnabled);
        Assert.IsTrue(initial.CanEdit);
        Assert.IsFalse(File.Exists(store.SettingsPath));

        Assert.AreEqual(DiagnosticSettingsStatus.Available, store.Save(false).Status);
        Assert.IsFalse(store.Load().Settings.IsEnabled);
        File.WriteAllText(store.SettingsPath, "{ broken");
        var corrupted = store.Load();
        Assert.AreEqual(DiagnosticSettingsStatus.Corrupted, corrupted.Status);
        Assert.IsFalse(corrupted.Settings.IsEnabled);
        Assert.IsFalse(corrupted.CanEdit);
        Assert.AreEqual("{ broken", File.ReadAllText(store.SettingsPath));
    }

    [TestMethod]
    public async Task RepositoryExamplesLoadIntoCurrentSchema()
    {
        var repository = FindRepositoryRoot();
        var examples = Directory.GetFiles(Path.Combine(repository, "examples"), "*.json");
        Assert.HasCount(3, examples);
        foreach (var example in examples)
        {
            var workspace = await new WorkspaceStore(example).LoadAsync();
            Assert.AreEqual(WorkspaceStore.CurrentSchemaVersion, workspace.SchemaVersion, example);
            Assert.IsNotEmpty(workspace.Projects, example);
        }
    }

    private static Exception Capture(Exception exception)
    {
        try { throw exception; }
        catch (Exception captured) { return captured; }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TalosDesk.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TalosDesk.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
