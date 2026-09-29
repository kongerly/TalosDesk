using System.Collections.ObjectModel;

namespace TalosDesk.Core.Updates;

public enum UpdateCheckPhase
{
    Idle,
    Checking,
    UpdateAvailable,
    NoUpdate,
    NoRelease,
    Failed,
    RateLimited,
    Cancelled,
    InvalidLocalVersion
}

public enum UpdateCheckSource
{
    None,
    Automatic,
    Manual
}

public sealed record UpdateHistoricalResult(
    UpdateCheckPhase Phase,
    UpdateCandidate? Candidate,
    DateTimeOffset CheckedAtUtc);

public sealed record UpdatePreferencesSnapshot(
    bool AutomaticCheckEnabled,
    UpdateChannelPreference Channel,
    IReadOnlyList<string> SkippedVersions,
    IReadOnlyDictionary<string, DateTimeOffset> RemindAfterUtc)
{
    internal static UpdatePreferencesSnapshot From(UpdatePreferences preferences) => new(
        preferences.AutomaticCheckEnabled,
        preferences.Channel,
        Array.AsReadOnly(preferences.SkippedVersions.ToArray()),
        new ReadOnlyDictionary<string, DateTimeOffset>(
            new Dictionary<string, DateTimeOffset>(preferences.RemindAfterUtc, StringComparer.Ordinal)));
}

public sealed record UpdateCoordinatorState(
    UpdateCheckPhase Phase,
    UpdateFetchStatus? FetchStatus,
    UpdatePreferencesSnapshot Preferences,
    bool PreferencesEditable,
    UpdateCandidate? Candidate,
    bool IsHistorical,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? RetryAfterUtc,
    bool ReminderEligible,
    string? Warning,
    UpdateCheckSource CheckSource = UpdateCheckSource.None,
    UpdateHistoricalResult? LastSuccessfulResult = null);

public sealed class UpdateCheckCoordinator : IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
    private readonly object _sync = new();
    private readonly SemanticVersion? _currentVersion;
    private readonly ReleaseChannel _currentChannel;
    private readonly GitHubReleaseClient _client;
    private readonly UpdateStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _shownReminders = new(StringComparer.Ordinal);
    private UpdatePreferences _preferences = new();
    private UpdateCache _cache = new();
    private CancellationTokenSource? _startupDelayCancellation;
    private Task<UpdateCoordinatorState>? _startupTask;
    private CancellationTokenSource? _activeCancellation;
    private Task<UpdateCoordinatorState>? _activeTask;
    private bool _activeIsManual;
    private bool _preferencesEditable = true;
    private bool _startupAutomaticEnabled;
    private bool _initialized;
    private bool _disposed;
    private long _generation;
    private long? _lastRequestTimestamp;
    private UpdateCoordinatorState _state;

    public UpdateCheckCoordinator(
        string currentVersion,
        ReleaseChannel currentChannel,
        GitHubReleaseClient client,
        UpdateStateStore store,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentChannel = currentChannel;
        if (Enum.IsDefined(currentChannel)) SemanticVersion.TryParse(currentVersion, false, out _currentVersion);
        _state = CreateState(_currentVersion is null ? UpdateCheckPhase.InvalidLocalVersion : UpdateCheckPhase.Idle);
    }

    public event Action<UpdateCoordinatorState>? StateChanged;

    public UpdateCoordinatorState State
    {
        get { lock (_sync) return _state; }
    }

    public async Task<UpdateCoordinatorState> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _store.LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        var cache = await _store.LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        UpdateCoordinatorState state;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_initialized) return _state;
            _initialized = true;
            _preferencesEditable = settings.Status != UpdateFileStatus.Invalid;
            _preferences = settings.Value.Clone();
            if (!_preferencesEditable)
            {
                _preferences.AutomaticCheckEnabled = false;
                _preferences.Channel = UpdateChannelPreference.FollowCurrent;
            }
            _startupAutomaticEnabled = _preferences.AutomaticCheckEnabled;
            _cache = cache.Status == UpdateFileStatus.Invalid ? new UpdateCache() : cache.Value.Clone();
            var warning = JoinWarnings(settings.Warning, cache.Warning);
            state = CreateHistoricalState(warning);
            _state = state;
        }
        StateChanged?.Invoke(state);
        return state;
    }

    public Task<UpdateCoordinatorState> NotifyStartupReadyAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            EnsureInitialized();
            if (_disposed || !_startupAutomaticEnabled || _currentVersion is null) return Task.FromResult(_state);
            if (_startupTask is not null) return _startupTask;
            _startupDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startupTask = DelayAndCheckAutomaticallyAsync(_startupDelayCancellation.Token);
            return _startupTask;
        }
    }

    public Task<UpdateCoordinatorState> CheckManuallyAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            EnsureInitialized();
            ThrowIfDisposed();
            if (_currentVersion is null) return Task.FromResult(_state);
            if (_activeTask?.IsCompleted == true) _activeTask = null;
            if (_activeTask is not null)
            {
                _activeIsManual = true;
                _state = _state with { CheckSource = UpdateCheckSource.Manual, ReminderEligible = false };
                StateChanged?.Invoke(_state);
                return _activeTask;
            }
            return StartCheckLocked(isManual: true, cancellationToken);
        }
    }

    public async Task<UpdateSaveResult> SetAutomaticCheckEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        UpdatePreferences snapshot;
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            EnsurePreferencesEditable();
            _preferences.AutomaticCheckEnabled = enabled;
            snapshot = _preferences.Clone();
            if (!enabled)
            {
                if (_activeTask is null) _startupDelayCancellation?.Cancel();
                else if (!_activeIsManual) cancellation = _activeCancellation;
            }
            _state = _state with { Preferences = UpdatePreferencesSnapshot.From(snapshot), ReminderEligible = enabled && _state.ReminderEligible };
        }
        cancellation?.Cancel();
        StateChanged?.Invoke(State);
        var saved = await _store.SavePreferencesAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!saved.Succeeded) PublishWarning(saved.Warning);
        return saved;
    }

    public async Task<UpdateSaveResult> SetChannelAsync(UpdateChannelPreference channel, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        Task<UpdateCoordinatorState>? active;
        CancellationTokenSource? startupCancellation;
        CancellationTokenSource? activeCancellation;
        lock (_sync)
        {
            EnsurePreferencesEditable();
            _generation++;
            startupCancellation = _startupDelayCancellation;
            activeCancellation = _activeCancellation;
            active = _activeTask;
        }
        startupCancellation?.Cancel();
        activeCancellation?.Cancel();
        if (active is not null)
        {
            try { await active.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            lock (_sync)
            {
                if (ReferenceEquals(_activeTask, active))
                {
                    _activeTask = null;
                    _activeCancellation?.Dispose();
                    _activeCancellation = null;
                }
            }
        }

        UpdatePreferences snapshot;
        UpdateCoordinatorState state;
        lock (_sync)
        {
            ThrowIfDisposed();
            _preferences.Channel = channel;
            snapshot = _preferences.Clone();
            state = CreateHistoricalState(_state.Warning);
            _state = state;
        }
        StateChanged?.Invoke(state);
        var saved = await _store.SavePreferencesAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!saved.Succeeded) PublishWarning(saved.Warning);
        return saved;
    }

    public Task<UpdateSaveResult> SkipVersionAsync(string versionIdentity, CancellationToken cancellationToken = default) =>
        ChangeReminderPreferenceAsync(versionIdentity, skip: true, cancellationToken);

    public Task<UpdateSaveResult> RemindLaterAsync(string versionIdentity, CancellationToken cancellationToken = default) =>
        ChangeReminderPreferenceAsync(versionIdentity, skip: false, cancellationToken);

    public void MarkReminderShown(string versionIdentity)
    {
        lock (_sync)
        {
            if (!IsCanonicalIdentity(versionIdentity)) throw new ArgumentException("版本身份无效。", nameof(versionIdentity));
            _shownReminders.Add(versionIdentity);
            _state = _state with { ReminderEligible = false };
        }
        StateChanged?.Invoke(State);
    }

    public void CancelForShutdown()
    {
        CancellationTokenSource? startupCancellation;
        CancellationTokenSource? activeCancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            startupCancellation = _startupDelayCancellation;
            activeCancellation = _activeCancellation;
        }
        startupCancellation?.Cancel();
        activeCancellation?.Cancel();
        _client.Dispose();
    }

    public void Dispose()
    {
        CancelForShutdown();
        _startupDelayCancellation?.Dispose();
        _activeCancellation?.Dispose();
    }

    private async Task<UpdateCoordinatorState> DelayAndCheckAutomaticallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            Task<UpdateCoordinatorState> task;
            lock (_sync)
            {
                if (_disposed || !_preferences.AutomaticCheckEnabled) return _state;
                if (_activeTask?.IsCompleted == true) _activeTask = null;
                task = _activeTask ?? StartCheckLocked(isManual: false, CancellationToken.None);
            }
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return State;
        }
    }

    private Task<UpdateCoordinatorState> StartCheckLocked(bool isManual, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var retry = GetRetryAfterLocked(now, isManual);
        if (retry is not null)
        {
            if (isManual)
            {
                _state = _state with { Phase = UpdateCheckPhase.RateLimited, FetchStatus = null, RetryAfterUtc = retry, Warning = null };
                _state = _state with { CheckSource = UpdateCheckSource.Manual, ReminderEligible = false };
                StateChanged?.Invoke(_state);
            }
            return Task.FromResult(_state);
        }

        _activeIsManual = isManual;
        _activeCancellation?.Dispose();
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var generation = ++_generation;
        _state = _state with
        {
            Phase = UpdateCheckPhase.Checking,
            FetchStatus = null,
            RetryAfterUtc = null,
            Warning = null,
            CheckSource = isManual ? UpdateCheckSource.Manual : UpdateCheckSource.Automatic,
            ReminderEligible = false
        };
        StateChanged?.Invoke(_state);
        _activeTask = RunCheckAsync(generation, _activeCancellation.Token);
        return _activeTask;
    }

    private async Task<UpdateCoordinatorState> RunCheckAsync(long generation, CancellationToken cancellationToken)
    {
        var attempt = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            _lastRequestTimestamp = _timeProvider.GetTimestamp();
            _cache.LastAttemptUtc = attempt;
        }
        var attemptSave = await _store.SaveCacheAsync(_cache, CancellationToken.None).ConfigureAwait(false);
        var fetch = await _client.FetchAsync(_cache.Pages, cancellationToken).ConfigureAwait(false);
        var completed = _timeProvider.GetUtcNow();
        UpdateCoordinatorState state;
        UpdateCache cacheToSave;
        lock (_sync)
        {
            if (fetch.Status == UpdateFetchStatus.Success)
            {
                _cache.Pages = fetch.Pages!.Select(page => page with { Releases = page.Releases.ToArray() }).ToList();
                _cache.LastSuccessUtc = completed;
                _cache.RateLimitUntilUtc = null;
            }
            else if (fetch.Status == UpdateFetchStatus.RateLimited)
            {
                _cache.RateLimitUntilUtc = fetch.RetryAfterUtc;
            }
            cacheToSave = _cache.Clone();
            var source = _activeIsManual ? UpdateCheckSource.Manual : UpdateCheckSource.Automatic;
            state = BuildResultState(fetch, generation == _generation, source, attemptSave.Warning);
            if (generation == _generation) _state = state;
        }
        var save = await _store.SaveCacheAsync(cacheToSave, CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            if (generation == _generation)
            {
                if (!save.Succeeded) _state = _state with { Warning = JoinWarnings(_state.Warning, save.Warning) };
                state = _state;
            }
            if (_activeTask is not null && generation == _generation)
            {
                _activeTask = null;
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }
        }
        if (generation == _generation) StateChanged?.Invoke(state);
        return state;
    }

    private UpdateCoordinatorState BuildResultState(
        UpdateFetchResult fetch,
        bool currentGeneration,
        UpdateCheckSource source,
        string? warning)
    {
        if (!currentGeneration) return _state;
        if (fetch.Status == UpdateFetchStatus.Success)
        {
            var selection = SelectFromCache();
            return selection.Status switch
            {
                UpdateSelectionStatus.UpdateAvailable => CreateState(UpdateCheckPhase.UpdateAvailable, selection.Candidate, false, warning, fetchStatus: fetch.Status, source: source),
                UpdateSelectionStatus.NoUpdate => CreateState(UpdateCheckPhase.NoUpdate, selection.Candidate, false, warning, fetchStatus: fetch.Status, source: source),
                UpdateSelectionStatus.NoRelease => CreateState(UpdateCheckPhase.NoRelease, warning: warning, fetchStatus: fetch.Status, source: source),
                _ => CreateState(UpdateCheckPhase.Failed, warning: JoinWarnings(warning, "更新发布地址无效。"), fetchStatus: UpdateFetchStatus.InvalidResponse, source: source)
            };
        }
        return fetch.Status switch
        {
            UpdateFetchStatus.RateLimited => CreateState(UpdateCheckPhase.RateLimited, retryAfterUtc: fetch.RetryAfterUtc, warning: warning, fetchStatus: fetch.Status, source: source),
            UpdateFetchStatus.Cancelled => CreateState(UpdateCheckPhase.Cancelled, warning: warning, fetchStatus: fetch.Status, source: source),
            _ => CreateState(UpdateCheckPhase.Failed, warning: warning, fetchStatus: fetch.Status, source: source)
        };
    }

    private DateTimeOffset? GetRetryAfterLocked(DateTimeOffset now, bool isManual)
    {
        if (_cache.RateLimitUntilUtc is { } serverLimit && serverLimit > now) return serverLimit;
        if (_lastRequestTimestamp is { } timestamp)
        {
            var remaining = MinimumRequestInterval - _timeProvider.GetElapsedTime(timestamp);
            if (remaining > TimeSpan.Zero) return now.Add(remaining);
        }
        else if (_cache.LastAttemptUtc is { } attempt && attempt <= now && now - attempt < MinimumRequestInterval)
        {
            return attempt.Add(MinimumRequestInterval);
        }
        if (!isManual && _cache.LastAttemptUtc is { } automaticAttempt && automaticAttempt <= now && now - automaticAttempt < AutomaticInterval)
            return automaticAttempt.Add(AutomaticInterval);
        return null;
    }

    private async Task<UpdateSaveResult> ChangeReminderPreferenceAsync(string versionIdentity, bool skip, CancellationToken cancellationToken)
    {
        if (!IsCanonicalIdentity(versionIdentity)) throw new ArgumentException("版本身份无效。", nameof(versionIdentity));
        UpdatePreferences snapshot;
        lock (_sync)
        {
            EnsurePreferencesEditable();
            if (skip)
            {
                if (!_preferences.SkippedVersions.Contains(versionIdentity, StringComparer.Ordinal)) _preferences.SkippedVersions.Add(versionIdentity);
                _preferences.RemindAfterUtc.Remove(versionIdentity);
            }
            else
            {
                _preferences.SkippedVersions.RemoveAll(value => string.Equals(value, versionIdentity, StringComparison.Ordinal));
                _preferences.RemindAfterUtc[versionIdentity] = _timeProvider.GetUtcNow().AddHours(24);
            }
            snapshot = _preferences.Clone();
            _state = _state with { Preferences = UpdatePreferencesSnapshot.From(snapshot), ReminderEligible = false };
        }
        StateChanged?.Invoke(State);
        var saved = await _store.SavePreferencesAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!saved.Succeeded) PublishWarning(saved.Warning);
        return saved;
    }

    private UpdateCoordinatorState CreateHistoricalState(string? warning)
    {
        if (_currentVersion is null) return CreateState(UpdateCheckPhase.InvalidLocalVersion, warning: warning);
        if (_cache.Pages.Count == 0) return CreateState(UpdateCheckPhase.Idle, warning: warning);
        var selection = SelectFromCache();
        return selection.Status switch
        {
            UpdateSelectionStatus.UpdateAvailable => CreateState(UpdateCheckPhase.UpdateAvailable, selection.Candidate, true, warning),
            UpdateSelectionStatus.NoUpdate => CreateState(UpdateCheckPhase.NoUpdate, selection.Candidate, true, warning),
            UpdateSelectionStatus.NoRelease => CreateState(UpdateCheckPhase.NoRelease, isHistorical: true, warning: warning),
            _ => CreateState(UpdateCheckPhase.Failed, isHistorical: true, warning: JoinWarnings(warning, "历史更新记录无效。"))
        };
    }

    private UpdateSelection SelectFromCache() => UpdateReleaseSelector.Select(
        _currentVersion!, _currentChannel, _preferences.Channel, _cache.Pages.SelectMany(page => page.Releases));

    private UpdateCoordinatorState CreateState(
        UpdateCheckPhase phase,
        UpdateCandidate? candidate = null,
        bool isHistorical = false,
        string? warning = null,
        DateTimeOffset? retryAfterUtc = null,
        UpdateFetchStatus? fetchStatus = null,
        UpdateCheckSource source = UpdateCheckSource.None)
    {
        var reminder = source == UpdateCheckSource.Automatic && !isHistorical &&
            _preferences.AutomaticCheckEnabled && phase == UpdateCheckPhase.UpdateAvailable && candidate is not null &&
            !_shownReminders.Contains(candidate.Version.Identity) &&
            !_preferences.SkippedVersions.Contains(candidate.Version.Identity, StringComparer.Ordinal) &&
            (!_preferences.RemindAfterUtc.TryGetValue(candidate.Version.Identity, out var until) || until <= _timeProvider.GetUtcNow());
        return new UpdateCoordinatorState(phase, fetchStatus, UpdatePreferencesSnapshot.From(_preferences), _preferencesEditable, candidate, isHistorical,
            _cache.LastAttemptUtc, _cache.LastSuccessUtc, retryAfterUtc ?? _cache.RateLimitUntilUtc, reminder, warning, source,
            CreateLastSuccessfulResult());
    }

    private UpdateHistoricalResult? CreateLastSuccessfulResult()
    {
        if (_currentVersion is null || _cache.LastSuccessUtc is not { } checkedAt || _cache.Pages.Count == 0) return null;
        var selection = SelectFromCache();
        return selection.Status switch
        {
            UpdateSelectionStatus.UpdateAvailable => new UpdateHistoricalResult(UpdateCheckPhase.UpdateAvailable, selection.Candidate, checkedAt),
            UpdateSelectionStatus.NoUpdate => new UpdateHistoricalResult(UpdateCheckPhase.NoUpdate, selection.Candidate, checkedAt),
            UpdateSelectionStatus.NoRelease => new UpdateHistoricalResult(UpdateCheckPhase.NoRelease, null, checkedAt),
            _ => null
        };
    }

    private void PublishWarning(string? warning)
    {
        if (warning is null) return;
        UpdateCoordinatorState state;
        lock (_sync)
        {
            _state = _state with { Warning = JoinWarnings(_state.Warning, warning) };
            state = _state;
        }
        StateChanged?.Invoke(state);
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("更新检查协调器尚未初始化。");
    }

    private void EnsurePreferencesEditable()
    {
        EnsureInitialized();
        if (!_preferencesEditable) throw new InvalidOperationException("更新偏好文件异常，当前会话不能修改设置。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsCanonicalIdentity(string value) =>
        SemanticVersion.TryParse(value, false, out var parsed) && string.Equals(parsed!.Identity, value, StringComparison.Ordinal);

    private static string? JoinWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second) || string.Equals(first, second, StringComparison.Ordinal)) return first;
        return first + " " + second;
    }
}
