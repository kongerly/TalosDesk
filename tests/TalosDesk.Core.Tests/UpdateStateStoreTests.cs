using TalosDesk.Core.Configuration;
using TalosDesk.Core.Updates;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class UpdateStateStoreTests
{
    [TestMethod]
    public async Task MissingFilesUseDefaultsWithoutCreatingFiles()
    {
        using var sandbox = new Sandbox();
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));

        var settings = await store.LoadPreferencesAsync();
        var cache = await store.LoadCacheAsync();

        Assert.AreEqual(UpdateFileStatus.Missing, settings.Status);
        Assert.IsFalse(settings.Value.AutomaticCheckEnabled);
        Assert.AreEqual(UpdateChannelPreference.FollowCurrent, settings.Value.Channel);
        Assert.AreEqual(UpdateFileStatus.Missing, cache.Status);
        Assert.IsFalse(File.Exists(store.SettingsPath));
        Assert.IsFalse(File.Exists(store.CachePath));
    }

    [TestMethod]
    public async Task SavesSettingsAndCompleteCacheBesideOnlyTheirWorkspace()
    {
        using var sandbox = new Sandbox();
        var first = new UpdateStateStore(Path.Combine(sandbox.Path, "one", "workspace.json"));
        var second = new UpdateStateStore(Path.Combine(sandbox.Path, "two", "workspace.json"));
        var preferences = new UpdatePreferences
        {
            AutomaticCheckEnabled = true,
            Channel = UpdateChannelPreference.IncludePreview,
            SkippedVersions = ["0.2.0"],
            RemindAfterUtc = new() { ["0.3.0-preview.1"] = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero) }
        };
        var page = new UpdateCachePage(GitHubReleaseClient.InitialRequestUri, "\"etag\"", null, [Release("0.2.0")]);
        var cache = new UpdateCache
        {
            LastAttemptUtc = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            LastSuccessUtc = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            Pages = [page]
        };

        Assert.IsTrue((await first.SavePreferencesAsync(preferences)).Succeeded);
        Assert.IsTrue((await first.SaveCacheAsync(cache)).Succeeded);

        Assert.AreEqual(UpdateFileStatus.Loaded, (await first.LoadPreferencesAsync()).Status);
        Assert.AreEqual("0.2.0", (await first.LoadCacheAsync()).Value.Pages.Single().Releases.Single().TagName);
        Assert.AreEqual(UpdateFileStatus.Missing, (await second.LoadPreferencesAsync()).Status);
        Assert.IsFalse(File.Exists(second.CachePath));
    }

    [TestMethod]
    public async Task InvalidFileIsPreservedAndDoesNotResetOtherFile()
    {
        using var sandbox = new Sandbox();
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));
        Directory.CreateDirectory(sandbox.Path);
        const string invalid = "{\"SchemaVersion\":99,\"private\":\"keep-original\"}";
        await File.WriteAllTextAsync(store.SettingsPath, invalid);
        await store.SaveCacheAsync(new UpdateCache());

        var settings = await store.LoadPreferencesAsync();

        Assert.AreEqual(UpdateFileStatus.Invalid, settings.Status);
        Assert.IsFalse(settings.Value.AutomaticCheckEnabled);
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(store.SettingsPath));
        Assert.AreEqual(UpdateFileStatus.Loaded, (await store.LoadCacheAsync()).Status);
    }

    [TestMethod]
    public async Task UnreadableSettingsAreReportedWithoutReplacingTheFile()
    {
        using var sandbox = new Sandbox();
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));
        await File.WriteAllTextAsync(store.SettingsPath, "{\"SchemaVersion\":1,\"AutomaticCheckEnabled\":false,\"Channel\":\"FollowCurrent\",\"SkippedVersions\":[],\"RemindAfterUtc\":{}}");
        using var lockStream = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var loaded = await store.LoadPreferencesAsync();

        Assert.AreEqual(UpdateFileStatus.Invalid, loaded.Status);
        Assert.IsFalse(loaded.Value.AutomaticCheckEnabled);
        Assert.AreEqual(lockStream.Length, new FileInfo(store.SettingsPath).Length);
    }

    [TestMethod]
    public async Task FailedAtomicWritePreservesExistingFileAndCleansTemporaryFile()
    {
        using var sandbox = new Sandbox();
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));
        await store.SavePreferencesAsync(new UpdatePreferences());
        var original = await File.ReadAllBytesAsync(store.SettingsPath);
        using var lockStream = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await store.SavePreferencesAsync(new UpdatePreferences { AutomaticCheckEnabled = true });

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(store.SettingsPath));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task CacheContainsOnlyReleaseProjectionAndNoWorkspaceContent()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace-secret.json");
        var store = new UpdateStateStore(workspace);
        await store.SaveCacheAsync(new UpdateCache { Pages = [new UpdateCachePage(GitHubReleaseClient.InitialRequestUri, null, null, [Release("0.2.0")])] });

        var text = await File.ReadAllTextAsync(store.CachePath);
        StringAssert.Contains(text, "TagName");
        Assert.IsFalse(text.Contains("workspace-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Body", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Authorization", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WorkspaceExportDoesNotIncludeUpdateSidecars()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var sidecars = new UpdateStateStore(workspace);
        await sidecars.SavePreferencesAsync(new UpdatePreferences { AutomaticCheckEnabled = true, SkippedVersions = ["0.2.0"] });
        await sidecars.SaveCacheAsync(new UpdateCache { Pages = [new UpdateCachePage(GitHubReleaseClient.InitialRequestUri, null, null, [Release("0.2.0")])] });
        var export = Path.Combine(sandbox.Path, "export.json");

        await WorkspaceStore.WriteExportToPathAsync(export, new WorkspaceConfiguration());

        var text = await File.ReadAllTextAsync(export);
        Assert.IsFalse(text.Contains("AutomaticCheckEnabled", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("SkippedVersions", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("UpdateCache", StringComparison.Ordinal));
    }

    private static UpdateRelease Release(string tag) =>
        new(tag, false, false, $"https://github.com/kongerly/TalosDesk/releases/tag/{tag}");

    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-update-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
