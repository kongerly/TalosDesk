using System.IO;
using System.Windows;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class WindowSettingsStoreTests
{
    [TestMethod]
    public void SettingsRoundTripIsIsolatedAndDoesNotModifyWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.WindowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = Path.Combine(root, "workspace.json");
            File.WriteAllText(workspace, "synthetic workspace");
            var store = new WindowSettingsStore(workspace);
            var settings = new WindowSettings(1100, 700, true, "synthetic-secondary", 40, 25);
            Assert.IsTrue(store.Save(settings));
            Assert.AreEqual(settings, store.Load());
            Assert.AreEqual(new WindowSettings(), new WindowSettingsStore(Path.Combine(root, "other.json")).Load());
            Assert.AreEqual("synthetic workspace", File.ReadAllText(workspace));
            Assert.IsFalse(File.Exists(store.FilePath + ".tmp"));
            Assert.DoesNotContain("IsValid", File.ReadAllText(store.FilePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidSettingsFallBackAndFailedSavePreservesPreviousData()
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.WindowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WindowSettingsStore(Path.Combine(root, "workspace.json"));
            foreach (var invalid in new[] { "{", "null", "{\"Width\":-1}", "{\"SchemaVersion\":2}", new string(' ', 4097) })
            {
                File.WriteAllText(store.FilePath, invalid);
                Assert.AreEqual(new WindowSettings(), store.Load());
            }
            Assert.IsTrue(store.Save(new WindowSettings(1000, 600)));
            var original = File.ReadAllText(store.FilePath);
            Assert.IsFalse(store.Save(new WindowSettings(double.NaN, 600)));
            Directory.CreateDirectory(store.FilePath + ".tmp");
            Assert.IsFalse(store.Save(new WindowSettings(1200, 700)));
            Assert.AreEqual(original, File.ReadAllText(store.FilePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestoreUsesMonitorRelativeLogicalCoordinatesAndClampsToWorkArea()
    {
        var screen = new ScreenArea("secondary", new Rect(-1920, -200, 1920, 1040), 1.5, false);
        var settings = new WindowSettings(800, 520, true, "secondary", 40, 30);
        Assert.AreEqual(new Rect(-1860, -155, 1200, 780), settings.RestoreBounds(screen, new Size(800, 520)));
        var moved = settings with { OffsetX = 2000, OffsetY = -2000 };
        Assert.AreEqual(new Rect(-1200, -200, 1200, 780), moved.RestoreBounds(screen, new Size(800, 520)));
        var smaller = screen with { WorkArea = new Rect(0, 0, 900, 600), Scale = 2 };
        Assert.AreEqual(smaller.WorkArea, settings.RestoreBounds(smaller, new Size(800, 520)));
    }

    [TestMethod]
    public void MissingMonitorCentersOnReplacementAndDpiChangePreservesLogicalSize()
    {
        var settings = new WindowSettings(800, 520, false, "disconnected", 150, 100);
        var primary = new ScreenArea("primary", new Rect(0, 0, 1920, 1080), 1, true);
        Assert.AreEqual(new Rect(560, 280, 800, 520), settings.RestoreBounds(primary, new Size(800, 520)));
        var scaled = primary with { Scale = 2, WorkArea = new Rect(0, 0, 3840, 2160) };
        Assert.AreEqual(new Rect(1120, 560, 1600, 1040), settings.RestoreBounds(scaled, new Size(800, 520)));
    }
}
