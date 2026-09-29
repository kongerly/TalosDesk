using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalosDesk.Core.Updates;

public sealed class UpdatePreferences
{
    public int SchemaVersion { get; set; } = 1;
    public bool AutomaticCheckEnabled { get; set; }
    public UpdateChannelPreference Channel { get; set; } = UpdateChannelPreference.FollowCurrent;
    public List<string> SkippedVersions { get; set; } = [];
    public Dictionary<string, DateTimeOffset> RemindAfterUtc { get; set; } = new(StringComparer.Ordinal);

    public UpdatePreferences Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        AutomaticCheckEnabled = AutomaticCheckEnabled,
        Channel = Channel,
        SkippedVersions = [.. SkippedVersions],
        RemindAfterUtc = new Dictionary<string, DateTimeOffset>(RemindAfterUtc, StringComparer.Ordinal)
    };
}

public sealed class UpdateCache
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public DateTimeOffset? RateLimitUntilUtc { get; set; }
    public List<UpdateCachePage> Pages { get; set; } = [];

    public UpdateCache Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        LastAttemptUtc = LastAttemptUtc,
        LastSuccessUtc = LastSuccessUtc,
        RateLimitUntilUtc = RateLimitUntilUtc,
        Pages = Pages.Select(page => page with { Releases = page.Releases.ToArray() }).ToList()
    };
}

public enum UpdateFileStatus
{
    Missing,
    Loaded,
    Invalid
}

public sealed record UpdateFileLoadResult<T>(UpdateFileStatus Status, T Value, string? Warning = null);

public sealed record UpdateSaveResult(bool Succeeded, string? Warning = null);

public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public UpdateStateStore(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) throw new ArgumentException("工作区路径不能为空。", nameof(workspacePath));
        WorkspacePath = Path.GetFullPath(workspacePath);
        SettingsPath = WorkspacePath + ".update-settings.json";
        CachePath = WorkspacePath + ".update-cache.json";
    }

    public string WorkspacePath { get; }
    public string SettingsPath { get; }
    public string CachePath { get; }

    public Task<UpdateFileLoadResult<UpdatePreferences>> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(SettingsPath, () => new UpdatePreferences(), ValidatePreferences, cancellationToken);

    public Task<UpdateFileLoadResult<UpdateCache>> LoadCacheAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(CachePath, () => new UpdateCache(), ValidateCache, cancellationToken);

    public Task<UpdateSaveResult> SavePreferencesAsync(UpdatePreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ValidatePreferences(preferences);
        return SaveAsync(SettingsPath, preferences.Clone(), cancellationToken);
    }

    public Task<UpdateSaveResult> SaveCacheAsync(UpdateCache cache, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ValidateCache(cache);
        return SaveAsync(CachePath, cache.Clone(), cancellationToken);
    }

    private static async Task<UpdateFileLoadResult<T>> LoadAsync<T>(
        string path,
        Func<T> createDefault,
        Action<T> validate,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new UpdateFileLoadResult<T>(UpdateFileStatus.Missing, createDefault());
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException();
            validate(value);
            return new UpdateFileLoadResult<T>(UpdateFileStatus.Loaded, value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return new UpdateFileLoadResult<T>(UpdateFileStatus.Invalid, createDefault(), "更新状态文件无法读取或格式不受支持。");
        }
    }

    private async Task<UpdateSaveResult> SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("更新状态路径没有父目录。");
            Directory.CreateDirectory(parent);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
            return new UpdateSaveResult(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new UpdateSaveResult(false, "更新状态未保存，重启后可能恢复原设置或记录。");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            _writeGate.Release();
        }
    }

    private static void ValidatePreferences(UpdatePreferences value)
    {
        if (value.SchemaVersion != 1 || !Enum.IsDefined(value.Channel) || value.SkippedVersions is null || value.RemindAfterUtc is null)
            throw new InvalidDataException();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in value.SkippedVersions)
        {
            if (!IsCanonicalIdentity(identity) || !identities.Add(identity)) throw new InvalidDataException();
        }
        foreach (var (identity, time) in value.RemindAfterUtc)
        {
            if (!IsCanonicalIdentity(identity) || time.Offset != TimeSpan.Zero) throw new InvalidDataException();
        }
    }

    private static void ValidateCache(UpdateCache value)
    {
        if (value.SchemaVersion != 1 || value.Pages is null || value.Pages.Count > 5 ||
            !IsUtc(value.LastAttemptUtc) || !IsUtc(value.LastSuccessUtc) || !IsUtc(value.RateLimitUntilUtc)) throw new InvalidDataException();
        for (var index = 0; index < value.Pages.Count; index++)
        {
            var page = value.Pages[index];
            if (page is null || page.Releases is null || !GitHubReleaseClient.ValidatePageUri(page.RequestUri, index + 1) ||
                !string.Equals(page.ApiVersion, GitHubReleaseClient.ApiVersion, StringComparison.Ordinal)) throw new InvalidDataException();
            var expectedNext = index + 1 < value.Pages.Count ? value.Pages[index + 1].RequestUri : null;
            if (!string.Equals(page.NextUri, expectedNext, StringComparison.Ordinal)) throw new InvalidDataException();
            foreach (var release in page.Releases)
            {
                if (release.TagName is null || release.HtmlUrl is null ||
                    SemanticVersion.TryParse(release.TagName, true, out _) && !ReleaseUriValidator.IsValid(release.HtmlUrl, release.TagName))
                    throw new InvalidDataException();
            }
        }
    }

    private static bool IsCanonicalIdentity(string? value) =>
        SemanticVersion.TryParse(value, false, out var version) && string.Equals(version!.Identity, value, StringComparison.Ordinal);

    private static bool IsUtc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero;
}
