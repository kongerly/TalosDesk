namespace TalosDesk.Core.Updates;

public enum ReleaseChannel
{
    Stable,
    Preview
}

public enum UpdateChannelPreference
{
    FollowCurrent,
    StableOnly,
    IncludePreview
}

public sealed record UpdateRelease(string TagName, bool Draft, bool Prerelease, string HtmlUrl);

public sealed record UpdateCandidate(SemanticVersion Version, UpdateRelease Release);

public enum UpdateSelectionStatus
{
    UpdateAvailable,
    NoUpdate,
    NoRelease,
    InvalidRelease
}

public sealed record UpdateSelection(UpdateSelectionStatus Status, UpdateCandidate? Candidate = null);

public static class UpdateReleaseSelector
{
    public static UpdateSelection Select(
        SemanticVersion currentVersion,
        ReleaseChannel currentChannel,
        UpdateChannelPreference preference,
        IEnumerable<UpdateRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(releases);
        var includePreview = preference switch
        {
            UpdateChannelPreference.FollowCurrent => currentChannel == ReleaseChannel.Preview,
            UpdateChannelPreference.StableOnly => false,
            UpdateChannelPreference.IncludePreview => true,
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };

        var eligible = new List<UpdateCandidate>();
        foreach (var release in releases)
        {
            if (release.Draft || !SemanticVersion.TryParse(release.TagName, true, out var version)) continue;
            if (!includePreview && (release.Prerelease || version!.IsPreRelease)) continue;
            if (!ReleaseUriValidator.IsValid(release.HtmlUrl, release.TagName))
                return new UpdateSelection(UpdateSelectionStatus.InvalidRelease);
            eligible.Add(new UpdateCandidate(version!, release));
        }

        if (eligible.Count == 0) return new UpdateSelection(UpdateSelectionStatus.NoRelease);
        var candidate = eligible.OrderByDescending(item => item.Version)
            .ThenBy(item => item.Release.Prerelease)
            .ThenBy(item => item.Release.TagName, StringComparer.Ordinal)
            .First();
        return candidate.Version.CompareTo(currentVersion) > 0
            ? new UpdateSelection(UpdateSelectionStatus.UpdateAvailable, candidate)
            : new UpdateSelection(UpdateSelectionStatus.NoUpdate, candidate);
    }
}

public static class ReleaseUriValidator
{
    private const string ReleasePrefix = "/kongerly/TalosDesk/releases/tag/";

    public static bool IsValid(string? value, string expectedTag)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (!uri.AbsolutePath.StartsWith(ReleasePrefix, StringComparison.Ordinal)) return false;
        var encodedTag = uri.AbsolutePath[ReleasePrefix.Length..];
        if (encodedTag.Length == 0 || encodedTag.Contains('/')) return false;
        try { return string.Equals(Uri.UnescapeDataString(encodedTag), expectedTag, StringComparison.Ordinal); }
        catch (UriFormatException) { return false; }
    }
}

public enum UpdateFetchStatus
{
    Success,
    NetworkError,
    InvalidResponse,
    SourceAddressError,
    Incomplete,
    RateLimited,
    Cancelled
}

public sealed record UpdateCachePage(
    string RequestUri,
    string? ETag,
    string? NextUri,
    IReadOnlyList<UpdateRelease> Releases,
    string ApiVersion = GitHubReleaseClient.ApiVersion);

public sealed record UpdateFetchResult(
    UpdateFetchStatus Status,
    IReadOnlyList<UpdateCachePage>? Pages = null,
    DateTimeOffset? RetryAfterUtc = null);
