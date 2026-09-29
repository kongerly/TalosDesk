using System.Net;
using System.Text;
using TalosDesk.Core.Updates;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class GitHubReleaseClientTests
{
    [TestMethod]
    public async Task ReadsValidatedPagesWithRequiredAnonymousHeaders()
    {
        var handler = new RecordingHandler((request, number, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.IsNull(request.Headers.Authorization);
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.AreEqual("TalosDesk", request.Headers.UserAgent.ToString());
            Assert.AreEqual(GitHubReleaseClient.ApiVersion, request.Headers.GetValues("X-GitHub-Api-Version").Single());
            var response = JsonResponse(number == 1 ? "0.2.0" : "0.3.0");
            if (number == 1)
                response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/kongerly/TalosDesk/releases?per_page=100&page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);

        var result = await client.FetchAsync();

        Assert.AreEqual(UpdateFetchStatus.Success, result.Status);
        Assert.HasCount(2, result.Pages!);
        Assert.AreEqual("0.3.0", result.Pages![1].Releases.Single().TagName);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task ReusesOnlyMatchingCachedPageForNotModifiedResponse()
    {
        var cached = new UpdateCachePage(GitHubReleaseClient.InitialRequestUri, "\"etag-one\"", null,
            [Release("0.2.0")]);
        var handler = new RecordingHandler((request, _, _) =>
        {
            if (request.Headers.IfNoneMatch.Count != 0)
                Assert.AreEqual("\"etag-one\"", request.Headers.IfNoneMatch.Single().ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);

        var success = await client.FetchAsync([cached]);
        var failure = await client.FetchAsync();

        Assert.AreEqual(UpdateFetchStatus.Success, success.Status);
        Assert.AreEqual("0.2.0", success.Pages!.Single().Releases.Single().TagName);
        Assert.AreEqual(UpdateFetchStatus.InvalidResponse, failure.Status);
    }

    [TestMethod]
    public async Task CombinesValidatedNotModifiedPageWithFreshFollowingPage()
    {
        const string secondUri = "https://api.github.com/repos/kongerly/TalosDesk/releases?per_page=100&page=2";
        var cached = new UpdateCachePage(GitHubReleaseClient.InitialRequestUri, "\"page-one\"", secondUri, [Release("0.2.0")]);
        var handler = new RecordingHandler((request, number, _) =>
        {
            if (number == 1)
            {
                Assert.AreEqual("\"page-one\"", request.Headers.IfNoneMatch.Single().ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            Assert.AreEqual(secondUri, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse("0.3.0"));
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);

        var result = await client.FetchAsync([cached]);

        Assert.AreEqual(UpdateFetchStatus.Success, result.Status);
        Assert.HasCount(2, result.Pages!);
        CollectionAssert.AreEqual(new[] { "0.2.0", "0.3.0" },
            result.Pages!.SelectMany(page => page.Releases).Select(release => release.TagName).ToArray());
    }

    [TestMethod]
    public async Task RejectsExternalPaginationWithoutRequestingIt()
    {
        var handler = new RecordingHandler((_, _, _) =>
        {
            var response = JsonResponse("0.2.0");
            response.Headers.TryAddWithoutValidation("Link", "<https://evil.example/releases?per_page=100&page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);

        var result = await client.FetchAsync();

        Assert.AreEqual(UpdateFetchStatus.SourceAddressError, result.Status);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task TreatsFifthPageWithNextLinkAsIncomplete()
    {
        var handler = new RecordingHandler((_, number, _) =>
        {
            var response = JsonResponse($"0.{number}.0");
            response.Headers.TryAddWithoutValidation("Link",
                $"<https://api.github.com/repos/kongerly/TalosDesk/releases?per_page=100&page={number + 1}>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);

        var result = await client.FetchAsync();

        Assert.AreEqual(UpdateFetchStatus.Incomplete, result.Status);
        Assert.HasCount(5, handler.Requests);
        Assert.IsNull(result.Pages);
    }

    [TestMethod]
    public async Task EnforcesCombinedDecompressedResponseLimit()
    {
        var oversized = new string('x', 5 * 1024 * 1024 + 1);
        var handler = new RecordingHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);

        Assert.AreEqual(UpdateFetchStatus.Incomplete, (await client.FetchAsync()).Status);
    }

    [TestMethod]
    public async Task ReturnsFixedStatusForMalformedResponseCancellationAndRateLimit()
    {
        var malformed = new RecordingHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"tag_name\":\"0.2.0\"}]", Encoding.UTF8, "application/json")
        }));
        using (var http = new HttpClient(malformed))
        using (var client = new GitHubReleaseClient(http))
            Assert.AreEqual(UpdateFetchStatus.InvalidResponse, (await client.FetchAsync()).Status);

        var limited = new RecordingHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            return Task.FromResult(response);
        });
        using (var http = new HttpClient(limited))
        using (var client = new GitHubReleaseClient(http))
        {
            var before = DateTimeOffset.UtcNow.AddSeconds(119);
            var result = await client.FetchAsync();
            Assert.AreEqual(UpdateFetchStatus.RateLimited, result.Status);
            Assert.IsGreaterThanOrEqualTo(before, result.RetryAfterUtc!.Value);
        }

        var blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new RecordingHandler((_, _, token) => blocked.Task.WaitAsync(token));
        using (var http = new HttpClient(waiting))
        using (var client = new GitHubReleaseClient(http))
        using (var cancellation = new CancellationTokenSource())
        {
            var task = client.FetchAsync(cancellationToken: cancellation.Token);
            cancellation.Cancel();
            Assert.AreEqual(UpdateFetchStatus.Cancelled, (await task).Status);
        }
    }

    [TestMethod]
    public async Task ClassifiesOfflineRedirectAndBudgetTimeoutWithoutLeakingExceptions()
    {
        var offline = new RecordingHandler((_, _, _) => throw new HttpRequestException("synthetic proxy detail"));
        using (var http = new HttpClient(offline))
        using (var client = new GitHubReleaseClient(http))
            Assert.AreEqual(UpdateFetchStatus.NetworkError, (await client.FetchAsync()).Status);

        var redirected = new RecordingHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://api.github.com/other") }
        }));
        using (var http = new HttpClient(redirected))
        using (var client = new GitHubReleaseClient(http))
            Assert.AreEqual(UpdateFetchStatus.SourceAddressError, (await client.FetchAsync()).Status);

        var slow = new RecordingHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("0.2.0");
        });
        using (var http = new HttpClient(slow))
        using (var client = new GitHubReleaseClient(http, TimeProvider.System, TimeSpan.FromMilliseconds(20)))
            Assert.AreEqual(UpdateFetchStatus.Incomplete, (await client.FetchAsync()).Status);
    }

    private static HttpResponseMessage JsonResponse(string tag)
    {
        var json = $"[{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":false," +
            $"\"html_url\":\"https://github.com/kongerly/TalosDesk/releases/tag/{tag}\"}}]";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{tag}\"");
        return response;
    }

    private static UpdateRelease Release(string tag) =>
        new(tag, false, false, $"https://github.com/kongerly/TalosDesk/releases/tag/{tag}");

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int _count;
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return responder(request, Interlocked.Increment(ref _count), cancellationToken);
        }
    }
}
