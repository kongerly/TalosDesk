using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App;

public partial class MainWindow
{
    private readonly ConcurrentDictionary<ICommandRunSession, Task> _sessionFinalizations = new();

    private static TcpProbeSnapshot GetProbe(ICommandRunSession session) => session is CommandRunSession concrete
        ? concrete.TcpProbe : new(TcpProbeState.NotConfigured, 0, null);

    private static bool IsPendingRecovery(CommandRecoverySnapshot? snapshot) => snapshot is { IsBusy: true } &&
        (snapshot.State is CommandRecoveryState.Waiting or CommandRecoveryState.Starting ||
         snapshot.RoundId is not null && (snapshot.State == CommandRecoveryState.Finalizing ||
         snapshot.State == CommandRecoveryState.Running && snapshot.Session?.Completion.IsCompleted == true));

    private static bool IsPendingRetry(CommandRecoverySnapshot? snapshot) => snapshot?.RoundId is not null && snapshot.IsBusy &&
        (snapshot.State is CommandRecoveryState.Waiting or CommandRecoveryState.Starting ||
         snapshot.State is CommandRecoveryState.Running or CommandRecoveryState.Finalizing &&
         snapshot.Session?.Completion.IsCompleted == true && snapshot.LastResult is { State: CommandRunState.Failed, ExitCode: not 0 } &&
         snapshot.Session.IsStopRequested != true);

    private static string FormatRecoveryConfiguration(CommandDefinition command) => command.AutoRestartEnabled
        ? "自动重启：开启，最多追加 3 次，分别等待 2、4、8 秒；仅独立运行生效，分组运行不生效"
        : "自动重启：关闭（替换时将关闭原策略）；仅独立运行生效，分组运行不生效";

    private static string? RecoveryStatus(CommandRecoverySnapshot? snapshot)
    {
        if (snapshot is null) return null;
        var used = snapshot.RoundId is null ? string.Empty : $"（已用 {snapshot.Attempts}/3 次）";
        var result = snapshot.LastResult is { } last ? $" · 退出码 {last.ExitCode}" : string.Empty;
        return snapshot.State switch
        {
            CommandRecoveryState.Waiting => $"失败{result}；{Math.Ceiling(snapshot.RemainingDelay?.TotalSeconds ?? 0)} 秒后自动重启（第 {snapshot.Attempts + 1}/3 次）",
            CommandRecoveryState.Starting => snapshot.RoundId is null ? "正在启动" : $"正在提交重启{used}",
            CommandRecoveryState.Finalizing => $"正在完成日志{result}{used}",
            CommandRecoveryState.Exhausted => $"失败{result} · 自动重启已耗尽（3/3）",
            CommandRecoveryState.OwnershipUnconfirmed => $"自动重启已中止 · 归属未确认{(snapshot.Error == CommandRecoveryError.StopFailed ? " · 停止失败，可再次停止" : string.Empty)}{used}{result}",
            CommandRecoveryState.Aborted => $"{(snapshot.RoundId is null ? "启动已中止" : "自动重启已中止")} · {ErrorLabel(snapshot.Error)}{used}{result}",
            CommandRecoveryState.Cancelled when snapshot.Session is null && snapshot.RoundId is not null => $"已取消自动重启{used}{result}",
            _ => null
        };
    }

    private static string ErrorLabel(CommandRecoveryError error) => error switch
    {
        CommandRecoveryError.PreflightFailed => "预检失败",
        CommandRecoveryError.CreationFailed => "创建失败",
        CommandRecoveryError.ObservationFailed => "观察失败",
        CommandRecoveryError.StopFailed => "停止失败",
        _ => "已取消"
    };

    private async Task<(ICommandRunSession? Session, string Error)> StartCommandSessionAsync(
        ProjectDefinition project, CommandDefinition command, CommandStartSource source,
        bool allowReserved = false, bool restart = false, CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        if (!_canSave || _workspaceChangeInProgress || _isStoppingForClose || _windowClosed) return (null, string.Empty);
        if (project.Id == Guid.Empty || command.Id == Guid.Empty) return (null, "项目或命令 ID 不能为空（全零 GUID）。");
        if (!allowReserved && _reservedCommandIds.Contains(command.Id)) return (null, "这条命令正在等待顺序分组执行。");
        var request = new CommandStartRequest(command.Id, command.Kind, source,
            () => PrepareRun(project, command), command.AutoRestartEnabled,
            async (session, _) =>
            {
                if (_sessionFinalizations.TryGetValue(session, out var finalization)) await finalization.ConfigureAwait(false);
            });
        try
        {
            var pending = restart ? _recovery.RestartAsync(request, cancellationToken) : _recovery.StartAsync(request, cancellationToken);
            RefreshCommandSelection();
            var session = await pending;
            // 成功创建的登记已排入同一 Dispatcher，在向分组交付真实 Completion 前完成登记。
            if (!_windowClosed) await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, CancellationToken.None);
            return (session, string.Empty);
        }
        catch (OperationCanceledException) { return (null, string.Empty); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_windowClosed) RefreshCommandSelection();
            return (null, exception is CommandEnvironmentException or InvalidDataException ? exception.Message :
                FormatCommandStartError(exception, command.WorkingDirectory, command.Shell));
        }
    }

    // Core 在后台调用。仅在 Dispatcher 上复制配置和基线；解密与文件检查使用此次副本。
    private CommandStartPlan PrepareRun(ProjectDefinition project, CommandDefinition command)
    {
        var input = Dispatcher.Invoke(() =>
        {
            if (_windowClosed || !_canSave || _isStoppingForClose || !Projects.Contains(project) || !project.Commands.Contains(command))
                throw new OperationCanceledException("配置已失效。");
            return (ProjectId: project.Id, Command: CloneCommand(command), Revision: _workspaceRevision, LogAvailable: _logStoreAvailable);
        });
        var current = _store.GetRevisionAsync().GetAwaiter().GetResult();
        var externalChange = Dispatcher.Invoke(() => !_windowClosed && !_workspaceChangeInProgress &&
            _workspaceRevision == input.Revision && input.Revision is { } expected && current != expected);
        if (externalChange)
        {
            // 当前轮次在提交之前失败，其他轮次也必须立即取消；提示回到 UI 线程处理。
            foreach (var snapshot in _recovery.GetSnapshots()) _recovery.CancelRecovery(snapshot.CommandId);
            Dispatcher.BeginInvoke(new Action(() => { if (!_windowClosed) ReportExternalWorkspaceChange(showDialog: false); }));
            throw new InvalidDataException("工作区文件已变更，请重新打开后运行。");
        }
        var definition = input.Command;
        CommandShellRules.Validate(definition.Shell, definition.Command);
        TcpProbeConfiguration.ValidateCommand(definition);
        if (!Directory.Exists(definition.WorkingDirectory)) throw new DirectoryNotFoundException();
        var environment = CommandRunEnvironmentResolver.Resolve(definition);
        var attempt = new RunAttempt(this, input.ProjectId, definition.Id, Interlocked.Increment(ref _nextRunVersion), input.LogAvailable);
        return new CommandStartPlan(definition.Command, definition.WorkingDirectory, attempt.OutputReceived, environment,
            definition.TcpProbe, attempt.SessionCreated, definition.Shell);
    }

    private sealed class RunAttempt(MainWindow owner, Guid projectId, Guid commandId, long version, bool logAvailable)
    {
        private readonly object _gate = new();
        private readonly List<CommandOutput> _earlyOutput = [];
        private RunLogWriter? _writer;
        private bool _created;
        private string? _warning;

        internal void OutputReceived(object? sender, CommandOutput output)
        {
            lock (_gate)
            {
                if (!_created) { _earlyOutput.Add(output); return; }
                Append(output);
            }
        }

        private void Append(CommandOutput output)
        {
            if (_writer is not null)
            {
                try { owner._runLogStore.Append(_writer, output); }
                catch { _writer.Info.WriteFailed = true; }
                if ((_writer.Info.WriteFailed || _writer.Info.Truncated) && Interlocked.Exchange(ref _writer.WarningReported, 1) == 0)
                {
                    _warning = _writer.Info.Truncated ? "日志达到容量上限，本批次后续输出未保存。" : "日志写入失败，本批次输出不完整。";
                    owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (owner._windowClosed || owner._runVersions.GetValueOrDefault(commandId) != version) return;
                        owner._logWarnings[commandId] = _warning;
                        owner.RefreshCommandSelection();
                    }));
                }
            }
            owner.QueueOutput(commandId, version, output);
        }

        internal void SessionCreated(ICommandRunSession session)
        {
            var startedAt = DateTimeOffset.Now;
            lock (_gate)
            {
                try
                {
                    if (!logAvailable) throw new IOException("日志目录或设置不可用。");
                    _writer = owner._runLogStore.Begin(projectId, commandId, startedAt);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                { _warning = "本次日志未保存。"; }
                // 界面登记优先于 Background 输出刷新。旧回调均核对单调版本及配置身份。
                owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (owner._windowClosed || owner._runVersions.GetValueOrDefault(commandId) >= version) return;
                    owner._runVersions[commandId] = version;
                    owner._sessions[commandId] = session;
                    owner._lastResults.Remove(commandId);
                    owner._runStartedAt[commandId] = startedAt;
                    owner.GetLogs(commandId).Clear();
                    owner._logWarnings.Remove(commandId);
                    if (_warning is not null) owner._logWarnings[commandId] = _warning;
                    if (_writer is not null) owner._runLogWriters[commandId] = _writer;
                    if (owner._selectedCommand?.Id == commandId) owner.RenderSelectedOutput(owner._selectedCommand, scrollToEnd: true);
                    owner.RefreshRunHistory(selectCurrent: true);
                    owner.RefreshCommandSelection();
                }));
                _created = true;
                foreach (var output in _earlyOutput) Append(output);
                _earlyOutput.Clear();
            }
            if (session is CommandRunSession concrete)
                concrete.TcpProbeChanged += (_, _) => owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!owner._windowClosed && owner._runVersions.GetValueOrDefault(commandId) == version &&
                        owner._sessions.TryGetValue(commandId, out var current) && ReferenceEquals(current, session)) owner.RefreshProbeDisplay();
                }));
            var finalization = FinalizeAsync(session);
            owner._sessionFinalizations[session] = finalization;
            owner.Dispatcher.BeginInvoke(new Action(() => { if (!owner._windowClosed) owner._logFinalizations[commandId] = finalization; }));
        }

        private async Task FinalizeAsync(ICommandRunSession session)
        {
            // 不在 Commit 的调用栈同步完成，以便先登记 finalization。
            await Task.Yield();
            CommandRunResult? result = null;
            try { result = await session.Completion.ConfigureAwait(false); }
            catch { /* 真实 Completion 保持失败，Core 保留归属；日志只记录中断。 */ }
            lock (_gate)
            {
                if (_writer is not null)
                    try { owner._runLogStore.Complete(_writer, result); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _warning = "日志完成写入失败。"; }
            }
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (owner._windowClosed || owner._runVersions.GetValueOrDefault(commandId) != version) return;
                owner._runLogWriters.Remove(commandId);
                if (session.IsOwnershipReleased && owner._sessions.TryGetValue(commandId, out var current) && ReferenceEquals(current, session)) owner._sessions.Remove(commandId);
                if (result is not null && owner.Projects.Any(project => project.Commands.Any(command => command.Id == commandId))) owner._lastResults[commandId] = result;
                if (_warning is not null) owner._logWarnings[commandId] = _warning;
                if (owner._selectedCommand?.Id == commandId) owner.RefreshRunHistory(selectCurrent: false);
                owner.RefreshCommandSelection();
            });
            owner._sessionFinalizations.TryRemove(session, out _);
        }
    }
}
