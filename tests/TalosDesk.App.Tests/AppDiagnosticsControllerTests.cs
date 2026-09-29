using System.IO;
using TalosDesk.Core.Diagnostics;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class AppDiagnosticsControllerTests
{
    [TestMethod]
    public void FatalPathWritesOnceAndTerminatesOnce()
    {
        using var sandbox = new TemporaryDirectory();
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"));
        var exits = new List<int>();
        var controller = new AppDiagnosticsController(store,
            new DiagnosticSettingsResult(new DiagnosticSettings(), DiagnosticSettingsStatus.Available, true),
            "0.2.0", "Preview", exits.Add);

        controller.RecordFatal(new InvalidOperationException("secret-one"), CrashSource.Dispatcher);
        controller.RecordFatal(new InvalidOperationException("secret-two"), CrashSource.AppDomain);

        CollectionAssert.AreEqual(new[] { 1 }, exits);
        var records = store.List().Records;
        Assert.HasCount(1, records);
        var record = store.Read(records[0].FileName).Record!;
        Assert.IsTrue(record.IsFatal);
        Assert.AreEqual(CrashSource.Dispatcher, record.Source);
        Assert.IsFalse(File.ReadAllText(Path.Combine(store.RootPath, records[0].FileName)).Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnobservedPathWritesAtMostOnceAndDoesNotTerminate()
    {
        using var sandbox = new TemporaryDirectory();
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"));
        var exits = 0;
        var controller = new AppDiagnosticsController(store,
            new DiagnosticSettingsResult(new DiagnosticSettings(), DiagnosticSettingsStatus.Available, true),
            "0.2.0", "Preview", _ => exits++);

        controller.RecordUnobserved(new AggregateException(new InvalidOperationException("first")));
        controller.RecordUnobserved(new AggregateException(new InvalidOperationException("second")));

        Assert.AreEqual(0, exits);
        Assert.HasCount(1, store.List().Records);
        Assert.IsFalse(store.Read(store.List().Records[0].FileName).Record!.IsFatal);
    }

    [TestMethod]
    public void DisabledControllerDoesNotCreateDirectoryButCanBeEnabledInSession()
    {
        using var sandbox = new TemporaryDirectory();
        var store = new CrashRecordStore(Path.Combine(sandbox.Path, "workspace.json"));
        var controller = new AppDiagnosticsController(store,
            new DiagnosticSettingsResult(new DiagnosticSettings(IsEnabled: false), DiagnosticSettingsStatus.Available, true),
            "0.2.0", "Preview", _ => { });

        controller.RecordUnobserved(new InvalidOperationException());
        Assert.IsFalse(Directory.Exists(store.RootPath));

        controller.IsEnabled = true;
        var secondController = new AppDiagnosticsController(store,
            new DiagnosticSettingsResult(new DiagnosticSettings(), DiagnosticSettingsStatus.Available, true),
            "0.2.0", "Preview", _ => { });
        secondController.RecordUnobserved(new InvalidOperationException());
        Assert.HasCount(1, store.List().Records);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TalosDesk.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
