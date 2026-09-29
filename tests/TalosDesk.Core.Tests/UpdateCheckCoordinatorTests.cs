using System.Net;
using System.Text;
using TalosDesk.Core.Updates;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class UpdateCheckCoordinatorTests
{
    [TestMethod]
    public async Task InvalidLocalVersionDisablesChecksWithoutSendingRequest()
    {
        using var sandbox = new Sandbox();
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);
        using var coordinator = new UpdateCheckCoordinator("not-a-version", ReleaseChannel.Preview, client,
            new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json")));

        Assert.AreEqual(UpdateCheckPhase.InvalidLocalVersion, (await coordinator.InitializeAsync()).Phase);
        Assert.AreEqual(UpdateCheckPhase.InvalidLocalVersion, (await coordinator.CheckManuallyAsync()).Phase);
        Assert.HasCount(0, handler.Requests);
    }

    [TestMethod]
    public async Task ManualCheckPublishesCandidateAndPersistsCompleteResult()
    {
        using var sandbox = new Sandbox();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http, clock);
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));
        using var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store, clock);
        await coordinator.InitializeAsync();

        var state = await coordinator.CheckManuallyAsync();

        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, state.Phase);
        Assert.AreEqual("0.2.0", state.Candidate!.Version.Identity);
        Assert.IsFalse(state.ReminderEligible);
        Assert.IsFalse(state.IsHistorical);
        Assert.AreEqual(clock.GetUtcNow(), state.LastAttemptUtc);
        Assert.AreEqual(clock.GetUtcNow(), state.LastSuccessUtc);
        Assert.AreEqual(UpdateFileStatus.Loaded, (await store.LoadCacheAsync()).Status);
    }

    [TestMethod]
    public async Task RepeatedManualCheckObeysMinuteIntervalWithoutChangingAttemptTime()
    {
        using var sandbox = new Sandbox();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http, clock);
        using var coordinator = Coordinator(sandbox, client, clock);
        await coordinator.InitializeAsync();
        await coordinator.CheckManuallyAsync();
        var attempt = coordinator.State.LastAttemptUtc;

        var blocked = await coordinator.CheckManuallyAsync();
        clock.Advance(TimeSpan.FromSeconds(60));
        var allowed = await coordinator.CheckManuallyAsync();

        Assert.AreEqual(UpdateCheckPhase.RateLimited, blocked.Phase);
        Assert.AreEqual(attempt, blocked.LastAttemptUtc);
        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, allowed.Phase);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task AutomaticCheckUsesTwentyFourHourBoundaryAndIgnoresFutureOrdinaryAttempt()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 23, 59, 0, TimeSpan.Zero));
        var store = new UpdateStateStore(workspace);
        await store.SavePreferencesAsync(new UpdatePreferences { AutomaticCheckEnabled = true });
        await store.SaveCacheAsync(new UpdateCache { LastAttemptUtc = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero) });
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using (var client = new GitHubReleaseClient(http, clock))
        using (var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store, clock))
        {
            await coordinator.InitializeAsync();
            var blocked = coordinator.NotifyStartupReadyAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(UpdateCheckPhase.Idle, (await blocked).Phase);
            Assert.HasCount(0, handler.Requests);
        }

        clock.Advance(TimeSpan.FromSeconds(55));
        using (var client = new GitHubReleaseClient(http, clock))
        using (var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store, clock))
        {
            await coordinator.InitializeAsync();
            var allowed = coordinator.NotifyStartupReadyAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, (await allowed).Phase);
            Assert.HasCount(1, handler.Requests);
        }

        await store.SaveCacheAsync(new UpdateCache { LastAttemptUtc = clock.GetUtcNow().AddDays(7) });
        clock.Advance(TimeSpan.FromSeconds(60));
        using var finalClient = new GitHubReleaseClient(http, clock);
        using var final = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, finalClient, store, clock);
        await final.InitializeAsync();
        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, (await final.CheckManuallyAsync()).Phase);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task ServerRateLimitBlocksManualAndAutomaticChecksUntilItsDeadline()
    {
        using var sandbox = new Sandbox();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var calls = 0;
        var handler = new TestHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.TryAddWithoutValidation("Retry-After", "120");
                return Task.FromResult(limited);
            }
            return Task.FromResult(Response("0.2.0"));
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http, clock);
        using var coordinator = Coordinator(sandbox, client, clock);
        await coordinator.InitializeAsync();

        var limitedState = await coordinator.CheckManuallyAsync();
        clock.Advance(TimeSpan.FromSeconds(60));
        var stillLimited = await coordinator.CheckManuallyAsync();
        clock.Advance(TimeSpan.FromSeconds(60));
        var allowed = await coordinator.CheckManuallyAsync();

        Assert.AreEqual(UpdateCheckPhase.RateLimited, limitedState.Phase);
        Assert.AreEqual(UpdateCheckPhase.RateLimited, stillLimited.Phase);
        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, allowed.Phase);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task EnablingAutomaticCheckTakesEffectOnlyOnNextCoordinatorStartup()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using (var client = new GitHubReleaseClient(http, clock))
        using (var first = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, new UpdateStateStore(workspace), clock))
        {
            await first.InitializeAsync();
            await first.SetAutomaticCheckEnabledAsync(true);
            await first.NotifyStartupReadyAsync();
            Assert.HasCount(0, handler.Requests);
        }

        using var secondClient = new GitHubReleaseClient(http, clock);
        using var second = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, secondClient, new UpdateStateStore(workspace), clock);
        await second.InitializeAsync();
        var startup = second.NotifyStartupReadyAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.HasCount(0, handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        var state = await startup;

        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, state.Phase);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task ManualCheckTakesOverAutomaticRequestAndSurvivesDisablingAutomaticChecks()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new UpdateStateStore(workspace);
        await store.SavePreferencesAsync(new UpdatePreferences { AutomaticCheckEnabled = true });
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var responseGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler((_, token) => responseGate.Task.WaitAsync(token));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http, clock);
        using var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store, clock);
        await coordinator.InitializeAsync();
        var automatic = coordinator.NotifyStartupReadyAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await handler.RequestStarted.Task;

        var manual = coordinator.CheckManuallyAsync();
        await coordinator.SetAutomaticCheckEnabledAsync(false);
        responseGate.SetResult(Response("0.2.0"));

        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, (await manual).Phase);
        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, (await automatic).Phase);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task ChannelChangeCancelsOldGenerationAndLateResultCannotOverwriteState()
    {
        using var sandbox = new Sandbox();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new TestHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response("0.3.0-preview.1", prerelease: true);
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http, clock);
        using var coordinator = Coordinator(sandbox, client, clock);
        await coordinator.InitializeAsync();
        var first = coordinator.CheckManuallyAsync();
        await firstStarted.Task;

        await coordinator.SetChannelAsync(UpdateChannelPreference.IncludePreview);
        Assert.AreNotEqual(UpdateCheckPhase.UpdateAvailable, coordinator.State.Phase);
        clock.Advance(TimeSpan.FromSeconds(60));
        var second = await coordinator.CheckManuallyAsync();

        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, second.Phase);
        Assert.AreEqual("0.3.0-preview.1", second.Candidate!.Version.Identity);
        Assert.HasCount(2, handler.Requests);
        await first;
    }

    [TestMethod]
    public async Task ChannelChangeDuringStartupDelayCancelsAutomaticRequest()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new UpdateStateStore(workspace);
        await store.SavePreferencesAsync(new UpdatePreferences { AutomaticCheckEnabled = true });
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http, clock);
        using var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store, clock);
        await coordinator.InitializeAsync();
        var delayed = coordinator.NotifyStartupReadyAsync();

        await coordinator.SetChannelAsync(UpdateChannelPreference.StableOnly);
        clock.Advance(TimeSpan.FromSeconds(5));

        await delayed;
        Assert.AreEqual(UpdateChannelPreference.StableOnly, coordinator.State.Preferences.Channel);
        Assert.HasCount(0, handler.Requests);
    }

    [TestMethod]
    public async Task SkipAndRemindLaterSuppressOnlyAutomaticReminderForCandidate()
    {
        using var sandbox = new Sandbox();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http, clock);
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));
        await store.SavePreferencesAsync(new UpdatePreferences { AutomaticCheckEnabled = true });
        using var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store, clock);
        await coordinator.InitializeAsync();
        await coordinator.CheckManuallyAsync();

        await coordinator.RemindLaterAsync("0.2.0");
        Assert.IsFalse(coordinator.State.ReminderEligible);
        Assert.AreEqual("0.2.0", coordinator.State.Candidate!.Version.Identity);
        clock.Advance(TimeSpan.FromHours(24));
        await coordinator.SetChannelAsync(UpdateChannelPreference.IncludePreview);
        Assert.IsTrue(coordinator.State.ReminderEligible);

        await coordinator.SkipVersionAsync("0.2.0");
        Assert.IsFalse(coordinator.State.ReminderEligible);
    }

    [TestMethod]
    public async Task CorruptPreferencesDisableAutomaticAndEditingButManualCheckStillWorks()
    {
        using var sandbox = new Sandbox();
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));
        await File.WriteAllTextAsync(store.SettingsPath, "{\"SchemaVersion\":99}");
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);
        using var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store);

        var initial = await coordinator.InitializeAsync();
        var manual = await coordinator.CheckManuallyAsync();

        Assert.IsFalse(initial.PreferencesEditable);
        Assert.IsFalse(initial.Preferences.AutomaticCheckEnabled);
        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, manual.Phase);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.SetAutomaticCheckEnabledAsync(true));
    }

    [TestMethod]
    public async Task StateWriteFailuresDoNotDiscardSessionSettingsOrNetworkResult()
    {
        using var sandbox = new Sandbox();
        var store = new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json"));
        await store.SavePreferencesAsync(new UpdatePreferences());
        await store.SaveCacheAsync(new UpdateCache());
        var handler = new TestHandler((_, _) => Task.FromResult(Response("0.2.0")));
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);
        using var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store);
        await coordinator.InitializeAsync();
        using var settingsLock = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cacheLock = new FileStream(store.CachePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var settingSave = await coordinator.SetAutomaticCheckEnabledAsync(true);
        var checkedState = await coordinator.CheckManuallyAsync();

        Assert.IsFalse(settingSave.Succeeded);
        Assert.IsTrue(coordinator.State.Preferences.AutomaticCheckEnabled);
        Assert.AreEqual(UpdateCheckPhase.UpdateAvailable, checkedState.Phase);
        Assert.AreEqual(UpdateFetchStatus.Success, checkedState.FetchStatus);
        Assert.IsNotNull(checkedState.Warning);
    }

    [TestMethod]
    public async Task PartialNetworkFailureKeepsLastCompleteCacheAndItsSuccessTime()
    {
        using var sandbox = new Sandbox();
        var workspace = Path.Combine(sandbox.Path, "workspace.json");
        var store = new UpdateStateStore(workspace);
        var priorSuccess = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        await store.SaveCacheAsync(new UpdateCache
        {
            LastSuccessUtc = priorSuccess,
            Pages = [new UpdateCachePage(GitHubReleaseClient.InitialRequestUri, null, null,
                [new UpdateRelease("0.2.0", false, false, "https://github.com/kongerly/TalosDesk/releases/tag/0.2.0")])]
        });
        var calls = 0;
        var handler = new TestHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var first = Response("0.3.0");
                first.Headers.TryAddWithoutValidation("Link",
                    "<https://api.github.com/repos/kongerly/TalosDesk/releases?per_page=100&page=2>; rel=\"next\"");
                return Task.FromResult(first);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);
        using var coordinator = new UpdateCheckCoordinator("0.1.1", ReleaseChannel.Preview, client, store);
        await coordinator.InitializeAsync();

        var state = await coordinator.CheckManuallyAsync();
        var persisted = (await store.LoadCacheAsync()).Value;

        Assert.AreEqual(UpdateCheckPhase.Failed, state.Phase);
        Assert.AreEqual(UpdateFetchStatus.InvalidResponse, state.FetchStatus);
        Assert.AreEqual(priorSuccess, persisted.LastSuccessUtc);
        Assert.AreEqual("0.2.0", persisted.Pages.Single().Releases.Single().TagName);
        Assert.IsNotNull(persisted.LastAttemptUtc);
    }

    [TestMethod]
    public async Task ShutdownCancelsRequestWithoutWaitingForNetwork()
    {
        using var sandbox = new Sandbox();
        var handler = new TestHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response("0.2.0");
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubReleaseClient(http);
        using var coordinator = Coordinator(sandbox, client, TimeProvider.System);
        await coordinator.InitializeAsync();
        var checking = coordinator.CheckManuallyAsync();
        await handler.RequestStarted.Task;

        coordinator.CancelForShutdown();
        var state = await checking.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(UpdateCheckPhase.Cancelled, state.Phase);
    }

    private static UpdateCheckCoordinator Coordinator(Sandbox sandbox, GitHubReleaseClient client, TimeProvider clock) =>
        new("0.1.1", ReleaseChannel.Preview, client,
            new UpdateStateStore(Path.Combine(sandbox.Path, "workspace.json")), clock);

    private static HttpResponseMessage Response(string tag, bool prerelease = false)
    {
        var json = $"[{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":{prerelease.ToString().ToLowerInvariant()}," +
            $"\"html_url\":\"https://github.com/kongerly/TalosDesk/releases/tag/{tag}\"}}]";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            RequestStarted.TrySetResult();
            return response(request, cancellationToken);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = initial;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() { lock (_sync) return _utcNow; }
        public override long GetTimestamp() { lock (_sync) return _timestamp; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_sync) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                due = _timers.Where(timer => timer.IsDue(_timestamp)).ToList();
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private long _due = owner.GetTimestamp() + dueTime.Ticks;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    _due = owner._timestamp + dueTime.Ticks;
                    period = newPeriod;
                    return true;
                }
            }

            public bool IsDue(long timestamp) => !_disposed && timestamp >= _due;

            public void Fire()
            {
                lock (owner._sync)
                {
                    if (_disposed) return;
                    if (period == Timeout.InfiniteTimeSpan) _disposed = true;
                    else _due = owner._timestamp + period.Ticks;
                }
                callback(state);
            }

            public void Dispose() { lock (owner._sync) _disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-update-coordinator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
