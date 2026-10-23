using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Processes;

public enum CommandStartSource { Independent, Group }
public enum CommandRecoveryState { Starting, Running, Finalizing, Waiting, Cancelled, Completed, Exhausted, Aborted, OwnershipUnconfirmed }
public enum CommandRecoveryError { None, PreflightFailed, CreationFailed, ObservationFailed, StopFailed, LifecycleCancelled }

// Prepare 每次重新预检并解密；不把预检结果或敏感值保存在轮次状态中。
public sealed record CommandStartRequest(Guid CommandId, CommandKind Kind, CommandStartSource Source,
    Func<CommandStartPlan> Prepare, bool AutoRestartEnabled = false,
    Func<ICommandRunSession, CommandRunResult, Task>? FinalizeSession = null);

public sealed record CommandStartPlan(string Command, string WorkingDirectory,
    EventHandler<CommandOutput>? OutputReceived = null, CommandRunEnvironment? Environment = null,
    TcpProbeConfiguration? TcpProbe = null, Action<ICommandRunSession>? SessionCreated = null,
    CommandShell Shell = CommandShell.PowerShell);

public sealed record CommandRecoverySnapshot(Guid CommandId, Guid? RoundId, long SessionVersion,
    CommandRecoveryState State, int Attempts, bool IsBusy, ICommandRunSession? Session,
    CommandRunResult? LastResult, TimeSpan? RemainingDelay, CommandRecoveryError Error, bool LogFinalizationFailed);

/// <summary>单次会话之上的有限恢复。所有启动来源应共用同一个协调器和 Runner。</summary>
public sealed class CommandRecoveryCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Func<Guid, CommandStartPlan, ICommandRunSession> _create;
    private readonly TimeProvider _time;
    private bool _closed;
    private int _pauseCount;
    private TaskCompletionSource _resumed = CompletedSignal();
    private long _sessionVersion;

    public CommandRecoveryCoordinator(CommandRunner runner, TimeProvider? timeProvider = null)
        : this((id, plan) => runner.Start(id, plan.Command, plan.WorkingDirectory, plan.OutputReceived, plan.Environment, plan.TcpProbe, plan.Shell),
            timeProvider ?? TimeProvider.System)
    {
        ArgumentNullException.ThrowIfNull(runner);
    }

    internal CommandRecoveryCoordinator(Func<Guid, CommandStartPlan, ICommandRunSession> create, TimeProvider timeProvider)
    {
        _create = create;
        _time = timeProvider;
    }

    public CommandRecoverySnapshot? GetSnapshot(Guid commandId)
    {
        lock (_sync) return _entries.TryGetValue(commandId, out var entry) ? Snapshot(entry) : null;
    }

    public IReadOnlyList<CommandRecoverySnapshot> GetSnapshots()
    {
        lock (_sync) return _entries.Values.Select(Snapshot).ToArray();
    }

    public bool IsBusy(Guid commandId)
    {
        lock (_sync) return _entries.TryGetValue(commandId, out var entry) && Busy(entry);
    }

    public Task<ICommandRunSession> StartAsync(CommandStartRequest request, CancellationToken cancellationToken = default) =>
        BeginManual(request, false, cancellationToken);

    public Task<ICommandRunSession> RestartAsync(CommandStartRequest request, CancellationToken cancellationToken = default) =>
        BeginManual(request, true, cancellationToken);

    private Task<ICommandRunSession> BeginManual(CommandStartRequest request, bool restart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CommandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Prepare);
        lock (_sync)
        {
            if (_closed) throw new InvalidOperationException("启动入口已关闭。");
            _entries.TryGetValue(request.CommandId, out var previous);
            if (restart && previous?.ManualTask is { IsCompleted: false } pending) return pending;
            if (!restart && previous is not null && Busy(previous)) throw new InvalidOperationException("命令已占用。");
            if (previous?.Session is { IsOwnershipReleased: false } &&
                (previous.State == CommandRecoveryState.OwnershipUnconfirmed || previous.Session.Completion.IsFaulted))
                throw new InvalidOperationException("旧归属退出尚未确认，请先重试停止。");
            if (previous is not null) Cancel(previous, CommandRecoveryError.None);
            var entry = new Entry(request)
            {
                Session = previous?.Session, Observation = previous?.Observation ?? Task.CompletedTask,
                RoundId = previous?.RoundId, Attempts = previous?.Attempts ?? 0,
                SessionVersion = previous?.SessionVersion ?? 0, LastResult = previous?.LastResult,
                LogFinalizationFailed = previous?.LogFinalizationFailed ?? false
            };
            _entries[request.CommandId] = entry;
            var completion = new TaskCompletionSource<ICommandRunSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.ManualTask = completion.Task;
            // 预检和停止可等待；只有最终创建与登记在 _sync 内不可分割。
            _ = Task.Run(() => RunManualAsync(entry, completion, cancellationToken), CancellationToken.None);
            return completion.Task;
        }
    }

    private async Task RunManualAsync(Entry entry, TaskCompletionSource<ICommandRunSession> completion, CancellationToken cancellationToken)
    {
        CommandRecoveryError stage = CommandRecoveryError.PreflightFailed;
        try
        {
            var plan = entry.Request.Prepare();
            ICommandRunSession? old;
            lock (_sync) { CheckManual(entry, cancellationToken); old = entry.Session; }
            if (old is not null)
            {
                stage = CommandRecoveryError.StopFailed;
                await old.StopAsync(cancellationToken).ConfigureAwait(false);
                try { await old.Completion.ConfigureAwait(false); }
                catch when (old.IsOwnershipReleased) { /* 已明确清空的故障会话可以由用户重新运行。 */ }
                if (!old.IsOwnershipReleased) throw new InvalidOperationException("旧归属退出尚未确认。");
                await entry.Observation.ConfigureAwait(false);
            }
            lock (_sync)
            {
                CheckManual(entry, cancellationToken);
                stage = CommandRecoveryError.CreationFailed;
                Commit(entry, plan, false);
                completion.TrySetResult(entry.Session!);
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (Current(entry))
                {
                    if (exception is OperationCanceledException && (entry.Cancelled || cancellationToken.IsCancellationRequested))
                        Cancel(entry, entry.Error);
                    else Abort(entry, stage, exception);
                }
                if (exception is OperationCanceledException) completion.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
                else completion.TrySetException(exception);
            }
        }
    }

    private void CheckManual(Entry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed || !Current(entry) || entry.Cancelled) throw new OperationCanceledException("启动已取消。");
    }

    private void Commit(Entry entry, CommandStartPlan plan, bool automatic)
    {
        if (automatic) entry.Attempts++;
        var session = _create(entry.Request.CommandId, plan);
        entry.Session = session;
        if (!automatic)
        {
            entry.Attempts = 0;
            entry.LastResult = null;
            entry.LogFinalizationFailed = false;
            entry.RoundId = entry.Request.AutoRestartEnabled && entry.Request.Source == CommandStartSource.Independent && entry.Request.Kind == CommandKind.Service
                ? Guid.NewGuid() : null;
        }
        entry.SessionVersion = ++_sessionVersion;
        entry.State = CommandRecoveryState.Running;
        entry.Error = CommandRecoveryError.None;
        // 宿主只在成功创建后登记批次；登记异常不能丢失已创建会话及其停止能力。
        try { plan.SessionCreated?.Invoke(session); }
        catch { entry.LogFinalizationFailed = true; }
        entry.Observation = ObserveAsync(entry, session);
    }

    private async Task ObserveAsync(Entry entry, ICommandRunSession session)
    {
        // 即使替身同步完成，也必须在 Commit 登记 Observation 后才能处理结果。
        await Task.Yield();
        CommandRunResult result;
        try { result = await session.Completion.ConfigureAwait(false); }
        catch
        {
            lock (_sync)
            {
                if (!Matches(entry, session)) return;
                Abort(entry, CommandRecoveryError.ObservationFailed, null);
            }
            return;
        }
        lock (_sync)
        {
            if (Matches(entry, session))
            {
                entry.LastResult = result;
                if (!entry.Cancelled) entry.State = CommandRecoveryState.Finalizing;
            }
        }
        try
        {
            if (entry.Request.FinalizeSession is { } finalize) await finalize(session, result).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync) { if (Matches(entry, session)) entry.LogFinalizationFailed = true; }
        }
        lock (_sync)
        {
            if (!Matches(entry, session)) return;
            if (!session.IsOwnershipReleased)
            {
                Abort(entry, CommandRecoveryError.ObservationFailed, null);
                return;
            }
            entry.Session = null;
            if (entry.Cancelled) return;
            var eligible = entry.Request.AutoRestartEnabled && entry.Request.Source == CommandStartSource.Independent &&
                entry.Request.Kind == CommandKind.Service && result.State == CommandRunState.Failed && result.ExitCode != 0 && !session.IsStopRequested;
            if (_closed || !eligible) { entry.State = CommandRecoveryState.Completed; return; }
            if (entry.Attempts == 3) { entry.State = CommandRecoveryState.Exhausted; return; }
            entry.Delay = TimeSpan.FromSeconds(2 << entry.Attempts);
            entry.WaitStarted = _time.GetTimestamp();
            entry.State = CommandRecoveryState.Waiting;
            var cancellation = new CancellationTokenSource();
            entry.WaitCancellation = cancellation;
            entry.WaitTask = RetryAsync(entry, cancellation);
        }
    }

    private async Task RetryAsync(Entry entry, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        CommandRecoveryError stage = CommandRecoveryError.PreflightFailed;
        try
        {
            await Task.Delay(entry.Delay, _time, cancellationToken).ConfigureAwait(false);
            await Task.Yield(); // 计时器回调只唤醒等待，不在其调用栈中执行预检或创建。
            while (true)
            {
                Task resumed;
                lock (_sync)
                {
                    if (!CanRetry(entry)) return;
                    resumed = _resumed.Task;
                }
                await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (!CanRetry(entry)) return;
                    if (_pauseCount > 0) continue;
                    entry.State = CommandRecoveryState.Starting;
                }
                var plan = entry.Request.Prepare();
                lock (_sync)
                {
                    if (!CanRetry(entry)) return;
                    if (_pauseCount > 0) { entry.State = CommandRecoveryState.Waiting; continue; }
                    stage = CommandRecoveryError.CreationFailed;
                    Commit(entry, plan, true);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_sync) { if (CanRetry(entry)) Abort(entry, stage, exception); }
        }
        finally
        {
            lock (_sync)
            {
                cancellation.Dispose();
                if (ReferenceEquals(entry.WaitCancellation, cancellation)) entry.WaitCancellation = null;
            }
        }
    }

    // 非 async 入口确保调用者第一次 await 前就记录停止意图，覆盖已完成但尚未处理的回调。
    public Task StopAsync(Guid commandId, CancellationToken cancellationToken = default) => StopAsync(commandId, null, cancellationToken);

    public Task StopAsync(Guid commandId, ICommandRunSession? expectedSession, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(commandId, out var entry)) return Task.CompletedTask;
            if (expectedSession is not null && !ReferenceEquals(entry.Session, expectedSession)) return Task.CompletedTask;
            Cancel(entry, CommandRecoveryError.None);
            return StopEntryAsync(entry, entry.Session, cancellationToken);
        }
    }

    private async Task StopEntryAsync(Entry entry, ICommandRunSession? session, CancellationToken cancellationToken,
        Func<ICommandRunSession, Task>? stopSession = null)
    {
        if (session is null) return;
        try
        {
            if (stopSession is null) await session.StopAsync(cancellationToken).ConfigureAwait(false);
            else await stopSession(session).ConfigureAwait(false);
            lock (_sync)
            {
                if (Matches(entry, session) && session.Completion.IsCompleted && session.IsOwnershipReleased)
                {
                    if (!session.Completion.IsCompletedSuccessfully) entry.Session = null;
                    entry.State = CommandRecoveryState.Cancelled;
                    if (entry.Error != CommandRecoveryError.LifecycleCancelled) entry.Error = CommandRecoveryError.None;
                }
            }
        }
        catch
        {
            lock (_sync)
            {
                if (Matches(entry, session))
                {
                    entry.Error = CommandRecoveryError.StopFailed;
                    entry.State = session.IsOwnershipReleased ? CommandRecoveryState.Cancelled : CommandRecoveryState.OwnershipUnconfirmed;
                }
            }
            throw;
        }
    }

    // 先暂停再读取快照；释放暂停令牌即“返回应用”，使用原截止时间。
    public IDisposable PauseAutomaticStarts()
    {
        lock (_sync)
        {
            if (_pauseCount++ == 0) _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new PauseLease(this);
        }
    }

    // 返回停止任务前已永久关闭入口并取消所有轮次，不等待退避或预检。
    public Task CloseAndStopAsync(CancellationToken cancellationToken = default, Func<ICommandRunSession, Task>? stopSession = null)
    {
        lock (_sync)
        {
            _closed = true;
            foreach (var entry in _entries.Values) Cancel(entry, CommandRecoveryError.LifecycleCancelled);
            return Task.WhenAll(_entries.Values.Select(entry => CloseEntryAsync(entry, entry.Session, entry.Observation, cancellationToken, stopSession)).ToArray());
        }
    }

    private async Task CloseEntryAsync(Entry entry, ICommandRunSession? session, Task observation, CancellationToken cancellationToken,
        Func<ICommandRunSession, Task>? stopSession)
    {
        await StopEntryAsync(entry, session, cancellationToken, stopSession).ConfigureAwait(false);
        if (session is not null)
        {
            try { await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch when (session.Completion.IsFaulted && session.IsOwnershipReleased) { }
        }
        await observation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // 退出停止失败而保留窗口时，由宿主显式重新开放手动入口；已取消轮次永不恢复。
    public void ReopenForManualStarts()
    {
        lock (_sync) _closed = false;
    }

    // 外部配置变化先取消等待，正在运行的归属保留原停止入口。
    public void CancelRecovery(Guid commandId)
    {
        lock (_sync) { if (_entries.TryGetValue(commandId, out var entry)) Cancel(entry, CommandRecoveryError.LifecycleCancelled); }
    }

    private void Resume()
    {
        lock (_sync) { if (--_pauseCount == 0) _resumed.TrySetResult(); }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private bool Current(Entry entry) => _entries.TryGetValue(entry.Request.CommandId, out var current) && ReferenceEquals(entry, current);
    private bool Matches(Entry entry, ICommandRunSession session) => Current(entry) && ReferenceEquals(entry.Session, session);
    private bool CanRetry(Entry entry) => Current(entry) && !_closed && !entry.Cancelled &&
        entry.State is CommandRecoveryState.Waiting or CommandRecoveryState.Starting;
    private static bool Busy(Entry entry) => entry.ManualTask is { IsCompleted: false } ||
        entry.Session is { } session && (!session.Completion.IsCompleted || !session.IsOwnershipReleased) ||
        entry.State is CommandRecoveryState.Starting or CommandRecoveryState.Running or CommandRecoveryState.Finalizing or CommandRecoveryState.Waiting;

    private static void Cancel(Entry entry, CommandRecoveryError error)
    {
        entry.Cancelled = true;
        entry.State = CommandRecoveryState.Cancelled;
        entry.Error = error;
        entry.WaitCancellation?.Cancel();
    }

    private static void Abort(Entry entry, CommandRecoveryError error, Exception? exception)
    {
        if (exception is CommandCreationException creation)
        {
            entry.Session = creation.Ownership;
            _ = creation.Ownership.Completion.Exception;
        }
        entry.Cancelled = true;
        if (entry.Error == CommandRecoveryError.None) entry.Error = error;
        entry.State = entry.Session is { IsOwnershipReleased: false }
            ? CommandRecoveryState.OwnershipUnconfirmed : CommandRecoveryState.Aborted;
    }

    private CommandRecoverySnapshot Snapshot(Entry entry)
    {
        var remaining = entry.State == CommandRecoveryState.Waiting
            ? (TimeSpan?)TimeSpan.FromTicks(Math.Max(0, (entry.Delay - _time.GetElapsedTime(entry.WaitStarted)).Ticks)) : null;
        var result = entry.Session is { Completion.IsCompletedSuccessfully: true } session ? session.Completion.Result : entry.LastResult;
        return new(entry.Request.CommandId, entry.RoundId, entry.SessionVersion, entry.State, entry.Attempts, Busy(entry),
            entry.Session, result, remaining, entry.Error, entry.LogFinalizationFailed);
    }

    private sealed class Entry(CommandStartRequest request)
    {
        internal CommandStartRequest Request { get; } = request;
        internal Guid? RoundId;
        internal long SessionVersion;
        internal CommandRecoveryState State = CommandRecoveryState.Starting;
        internal CommandRecoveryError Error;
        internal int Attempts;
        internal bool Cancelled;
        internal bool LogFinalizationFailed;
        internal ICommandRunSession? Session;
        internal CommandRunResult? LastResult;
        internal Task<ICommandRunSession>? ManualTask;
        internal Task Observation = Task.CompletedTask;
        internal Task? WaitTask;
        internal CancellationTokenSource? WaitCancellation;
        internal TimeSpan Delay;
        internal long WaitStarted;
    }

    private sealed class PauseLease(CommandRecoveryCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Resume(); }
    }
}
