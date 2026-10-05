using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TalosDesk.Core.Updates;

public sealed class GitHubReleaseClient : IDisposable
{
    public const string ApiVersion = "2026-03-10";
    public const string InitialRequestUri = "https://api.github.com/repos/kongerly/TalosDesk/releases?per_page=100&page=1";
    private const int MaximumPages = 5;
    private const long MaximumResponseBytes = 5L * 1024 * 1024;
    private static readonly TimeSpan DefaultRequestBudget = TimeSpan.FromSeconds(15);
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestBudget;

    public GitHubReleaseClient(HttpClient httpClient, TimeProvider? timeProvider = null)
        : this(httpClient, timeProvider ?? TimeProvider.System, false, DefaultRequestBudget)
    {
    }

    internal GitHubReleaseClient(HttpClient httpClient, TimeProvider timeProvider, TimeSpan requestBudget)
        : this(httpClient, timeProvider, false, requestBudget)
    {
    }

    private GitHubReleaseClient(HttpClient httpClient, TimeProvider timeProvider, bool ownsClient, TimeSpan requestBudget)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider;
        _ownsClient = ownsClient;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestBudget, TimeSpan.Zero);
        _requestBudget = requestBudget;
    }

    public static GitHubReleaseClient CreateDefault(TimeProvider? timeProvider = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        return new GitHubReleaseClient(new HttpClient(handler, disposeHandler: true), timeProvider ?? TimeProvider.System, true, DefaultRequestBudget);
    }

    public async Task<UpdateFetchResult> FetchAsync(
        IReadOnlyList<UpdateCachePage>? cachedPages = null,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_requestBudget);
        var token = budget.Token;
        var pages = new List<UpdateCachePage>();
        var next = InitialRequestUri;
        long totalBytes = 0;

        try
        {
            for (var pageNumber = 1; pageNumber <= MaximumPages; pageNumber++)
            {
                var cached = cachedPages?.FirstOrDefault(page => string.Equals(page.RequestUri, next, StringComparison.Ordinal) &&
                    string.Equals(page.ApiVersion, ApiVersion, StringComparison.Ordinal));
                using var request = CreateRequest(next, cached?.ETag);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                    HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                    return new UpdateFetchResult(UpdateFetchStatus.SourceAddressError);

                if ((int)response.StatusCode is 403 or 429)
                    return new UpdateFetchResult(UpdateFetchStatus.RateLimited, RetryAfterUtc: GetRetryAfter(response));

                UpdateCachePage resultPage;
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (cached is null || !EntityTagHeaderValue.TryParse(cached.ETag, out _) || !ValidateCachedPage(cached, pageNumber))
                        return new UpdateFetchResult(UpdateFetchStatus.InvalidResponse);
                    resultPage = cached;
                }
                else
                {
                    if (!response.IsSuccessStatusCode) return new UpdateFetchResult(UpdateFetchStatus.InvalidResponse);
                    var bytes = await ReadBoundedAsync(response.Content, totalBytes, token).ConfigureAwait(false);
                    totalBytes += bytes.Length;
                    if (!TryReadReleases(bytes, out var releases)) return new UpdateFetchResult(UpdateFetchStatus.InvalidResponse);
                    token.ThrowIfCancellationRequested();
                    var nextUri = ReadNextLink(response.Headers);
                    if (nextUri is not null && !ValidatePageUri(nextUri, pageNumber + 1))
                        return new UpdateFetchResult(UpdateFetchStatus.SourceAddressError);
                    resultPage = new UpdateCachePage(next, response.Headers.ETag?.ToString(), nextUri, releases!, ApiVersion);
                }

                pages.Add(resultPage);
                if (resultPage.NextUri is null) return new UpdateFetchResult(UpdateFetchStatus.Success, pages);
                if (pageNumber == MaximumPages) return new UpdateFetchResult(UpdateFetchStatus.Incomplete);
                next = resultPage.NextUri;
            }
        }
        catch (ResponseLimitException)
        {
            return new UpdateFetchResult(UpdateFetchStatus.Incomplete);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateFetchResult(UpdateFetchStatus.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return new UpdateFetchResult(UpdateFetchStatus.Incomplete);
        }
        catch (HttpRequestException)
        {
            return new UpdateFetchResult(UpdateFetchStatus.NetworkError);
        }
        catch (IOException)
        {
            return new UpdateFetchResult(UpdateFetchStatus.NetworkError);
        }

        return new UpdateFetchResult(UpdateFetchStatus.Incomplete);
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    private static HttpRequestMessage CreateRequest(string requestUri, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TalosDesk");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var parsed))
            request.Headers.IfNoneMatch.Add(parsed);
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long bytesAlreadyRead, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bytesAlreadyRead + buffer.Length + read > MaximumResponseBytes) throw new ResponseLimitException();
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool TryReadReleases(byte[] bytes, out IReadOnlyList<UpdateRelease>? releases)
    {
        releases = null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            var result = new List<UpdateRelease>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryGetString(item, "tag_name", out var tag) ||
                    !TryGetBoolean(item, "draft", out var draft) ||
                    !TryGetBoolean(item, "prerelease", out var prerelease) ||
                    !TryGetString(item, "html_url", out var htmlUrl)) return false;
                result.Add(new UpdateRelease(tag!, draft, prerelease, htmlUrl!));
            }
            releases = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement item, string name, out string? value)
    {
        value = null;
        return item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            (value = property.GetString()) is not null;
    }

    private static bool TryGetBoolean(JsonElement item, string name, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static string? ReadNextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values)) return null;
        string? next = null;
        foreach (var entry in values.SelectMany(value => value.Split(',')))
        {
            var parts = entry.Trim().Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts.Skip(1).Any(part => string.Equals(part, "rel=\"next\"", StringComparison.OrdinalIgnoreCase))) continue;
            if (next is not null || parts[0].Length < 3 || parts[0][0] != '<' || parts[0][^1] != '>') return string.Empty;
            next = parts[0][1..^1];
        }
        return next;
    }

    internal static bool ValidatePageUri(string? value, int expectedPage)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.AbsolutePath, "/repos/kongerly/TalosDesk/releases", StringComparison.Ordinal)) return false;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (query.Length != 2) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query)
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !values.TryAdd(pair[0], pair[1])) return false;
        }
        return values.Count == 2 && values.GetValueOrDefault("per_page") == "100" &&
            values.GetValueOrDefault("page") == expectedPage.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ValidateCachedPage(UpdateCachePage page, int pageNumber)
    {
        if (!string.Equals(page.ApiVersion, ApiVersion, StringComparison.Ordinal) || !ValidatePageUri(page.RequestUri, pageNumber)) return false;
        if (page.ETag is not null && !EntityTagHeaderValue.TryParse(page.ETag, out _)) return false;
        if (page.NextUri is not null && !ValidatePageUri(page.NextUri, pageNumber + 1)) return false;
        return page.Releases.All(release => !SemanticVersion.TryParse(release.TagName, true, out _) ||
            ReleaseUriValidator.IsValid(release.HtmlUrl, release.TagName));
    }

    private DateTimeOffset GetRetryAfter(HttpResponseMessage response)
    {
        var now = _timeProvider.GetUtcNow();
        var candidates = new List<DateTimeOffset> { now.AddSeconds(60) };
        if (response.Headers.RetryAfter?.Date is { } date) candidates.Add(date);
        if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero) candidates.Add(now.Add(delta));
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0") &&
            response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
            long.TryParse(resets.FirstOrDefault(), out var seconds))
        {
            try { candidates.Add(DateTimeOffset.FromUnixTimeSeconds(seconds)); }
            catch (ArgumentOutOfRangeException) { }
        }
        return candidates.Max();
    }

    private sealed class ResponseLimitException : Exception;
}
