using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class RecoveryRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void BudgetLogsAndOccupancy()
    {
        using var f = new Fixture();
        f.Start();
        var round = f.Snapshot.RoundId;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = f.Latest;
            Wait(() => f.Output.Text.Contains($"attempt-{attempt + 1}", StringComparison.Ordinal));
            Assert.AreEqual(attempt, f.Snapshot.Attempts);
            f.Clock.Advance(1000); // 长时间运行不会归零。
            current.Finish(CommandRunState.Failed, 17);
            if (attempt == 3) break;
            Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
            Stage = $"第 {attempt + 1} 次等待：占用、输出与批次";
            Assert.AreEqual("0", f.Text("OverviewRunningCountText"));
            StringAssert.Contains(f.Text("OverviewProbeCountText"), "1 个待重试");
            Assert.IsFalse(f.Button("RunButton").IsEnabled);
            Assert.IsFalse(f.Button("EditButton").IsEnabled);
            Assert.IsFalse(f.Button("DeleteCommandButton").IsEnabled);
            Assert.IsFalse(f.Button("EditProjectButton").IsEnabled);
            Assert.IsFalse(f.Button("ImportButton").IsEnabled);
            Assert.IsTrue(f.Button("CopyButton").IsEnabled);
            Assert.IsTrue(f.Button("ExportButton").IsEnabled);
            Assert.IsTrue(f.Button("StopButton").IsEnabled);
            Assert.IsTrue(f.Button("RestartButton").IsEnabled);
            StringAssert.Contains(f.Output.Text, $"attempt-{attempt + 1}");
            Assert.HasCount(attempt + 1, Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
            var duplicate = f.StartTask();
            Wait(() => duplicate.IsCompleted);
            Assert.IsNull(duplicate.Result.Session);
            Assert.HasCount(attempt + 1, f.Sessions);
            var reload = f.Window.ReloadWorkspaceForSecondaryLaunchAsync();
            Wait(() => reload.IsCompleted);
            Assert.AreEqual(round, f.Snapshot.RoundId);
            if (attempt == 0)
            {
                Assert.IsFalse(Respond(() => (bool)Invoke(f.Window, "CanChangeLogLocation")!, dialog =>
                    ((Button)dialog.FindName("PrimaryActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                Capture(f.Window, "recovery-waiting");
                var width = f.Window.Width;
                var height = f.Window.Height;
                f.Window.Width = f.Window.MinWidth; f.Window.Height = f.Window.MinHeight; f.Window.UpdateLayout();
                Capture(f.Window, "recovery-waiting-narrow");
                Assert.IsTrue(f.Button("StopButton").IsVisible && f.Button("StopButton").ActualWidth > 0);
                f.Window.Width = width; f.Window.Height = height;
            }
            var delay = 2 << attempt;
            f.Clock.Advance(delay - 1);
            Assert.HasCount(attempt + 1, f.Sessions);
            f.Clock.Advance(1);
            Wait(() => f.Sessions.Count == attempt + 2 && f.Snapshot.State == CommandRecoveryState.Running);
        }
        Wait(() => f.Snapshot.State == CommandRecoveryState.Exhausted);
        Wait(() => f.Text("RunStateText").Contains("已耗尽", StringComparison.Ordinal), () => $"state={f.Snapshot.State}, text={f.Text("RunStateText")}, timer={Field<DispatcherTimer>(f.Window, "_recoveryTimer").IsEnabled}, closed={Field<bool>(f.Window, "_windowClosed")}");
        Assert.IsTrue(f.Button("RunButton").IsEnabled);
        Assert.IsFalse(f.Button("StopButton").IsEnabled);
        f.Clock.Advance(10000);
        Assert.HasCount(4, f.Sessions);
        Assert.HasCount(4, Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
        foreach (var path in Directory.GetFiles(f.Root, "stdout.log", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            StringAssert.Contains(text, "attempt-");
            Assert.DoesNotContain("自动重启", text);
        }
        f.Button("OutputPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var history = (ComboBox)f.Window.FindName("RunHistoryComboBox");
        history.SelectedIndex = 1;
        Wait(() => f.Output.Text.Contains("attempt-", StringComparison.Ordinal));
        Assert.DoesNotContain("自动重启", f.Text("OutputRunStateText"));
        Capture(f.Window, "recovery-exhausted-history");
    }

    internal static void PauseReturnAndWaitingExit()
    {
        using var f = new Fixture();
        f.Start(); f.Latest.Finish(CommandRunState.Failed, 8);
        Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
        var round = f.Snapshot.RoundId;
        f.Exit.Confirm = (_, pending) =>
        {
            Assert.AreEqual(1, pending);
            f.Clock.Advance(50);
            Pump();
            Assert.HasCount(1, f.Sessions);
            return false;
        };
        f.Window.Close();
        Assert.IsTrue(f.Window.IsVisible);
        Wait(() => f.Sessions.Count == 2);
        Assert.AreEqual(round, f.Snapshot.RoundId);
        Assert.AreEqual(1, f.Snapshot.Attempts);
        f.Latest.Finish(CommandRunState.Failed, 8);
        Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
        f.Exit.Confirm = (_, pending) => { Assert.AreEqual(1, pending); return true; };
        f.Window.Close();
        Wait(() => !f.Window.IsVisible);
        f.Clock.Advance(100);
        Assert.HasCount(2, f.Sessions);
        Assert.IsFalse(f.Coordinator.IsBusy(f.CommandId));
    }

    internal static void QueuedCompletionAndRestartCannotEscapeExit()
    {
        using var f = new Fixture();
        f.Start();
        // 不泵送 Dispatcher：真实 Completion 已结束，恢复与界面回调尚未完成。
        f.Latest.Finish(CommandRunState.Failed, 9);
        f.Exit.Confirm = (_, pending) => { Assert.AreEqual(1, pending); return true; };
        f.Window.Close();
        Wait(() => !f.Window.IsVisible);
        f.Clock.Advance(100);
        Assert.HasCount(1, f.Sessions);
        Assert.AreEqual(1, f.Exit.ConfirmCount);
        var output = f.Output.Text;
        var version = Field<Dictionary<Guid, long>>(f.Window, "_runVersions")[f.CommandId];
        Invoke(f.Window, "QueueOutput", f.CommandId, version, new CommandOutput(DateTimeOffset.Now, "stdout", "late-after-close"));
        Pump();
        Assert.AreEqual(output, f.Output.Text, "关闭后的旧输出回调不能写入窗口。");

        using var restarting = new Fixture();
        restarting.Start();
        // 保持在同一 UI 调用栈，不处理后台 Prepare 的 Dispatcher 请求。
        var manualRestart = restarting.StartTask(restart: true);
        restarting.Exit.Confirm = (_, pending) => { Assert.AreEqual(1, pending); return true; };
        restarting.Window.Close();
        Wait(() => !restarting.Window.IsVisible && manualRestart.IsCompleted);
        Assert.IsNull(manualRestart.GetAwaiter().GetResult().Session);
        restarting.Clock.Advance(100);
        Assert.HasCount(1, restarting.Sessions);
        Assert.IsTrue(restarting.Latest.IsOwnershipReleased);
    }

    internal static void ManualRestartAndStopCancelOldRound()
    {
        using var f = new Fixture();
        f.Start(); f.Latest.Finish(CommandRunState.Failed, 7);
        Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
        var old = f.Snapshot.RoundId;
        f.Start(restart: true);
        Assert.AreNotEqual(old, f.Snapshot.RoundId);
        Assert.AreEqual(0, f.Snapshot.Attempts);
        f.Clock.Advance(20);
        Assert.HasCount(2, f.Sessions);
        f.Latest.Finish(CommandRunState.Failed, 7);
        Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
        f.Button("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(() => !f.Coordinator.IsBusy(f.CommandId));
        f.Clock.Advance(100);
        Assert.HasCount(2, f.Sessions);
        Wait(() => f.Text("RunStateText").Contains("已取消", StringComparison.Ordinal));
    }

    internal static void ExternalChangeCancelsBeforeAutomaticCreate()
    {
        using var f = new Fixture();
        f.Start(); f.Latest.Finish(CommandRunState.Failed, 7);
        Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
        File.AppendAllText(f.Store.FilePath, " ");
        f.Clock.Advance(2);
        Wait(() => !f.Coordinator.IsBusy(f.CommandId));
        Wait(() => !f.Button("RunButton").IsEnabled);
        Assert.HasCount(1, f.Sessions);
        Assert.EndsWith(" ", File.ReadAllText(f.Store.FilePath));
        Assert.AreEqual(CommandRecoveryState.Cancelled, f.Snapshot.State);
    }

    internal static void AutomaticPreflightAndCreationFailuresKeepLastBatch()
    {
        foreach (var preflight in new[] { true, false })
        {
            using var f = new Fixture();
            f.Start(); f.Latest.Finish(CommandRunState.Failed, 7);
            Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
            if (preflight) Directory.Delete(f.WorkingDirectory); else f.CreateFailure = new IOException("synthetic creation failure");
            f.Clock.Advance(2);
            Wait(() => f.Snapshot.State == CommandRecoveryState.Aborted);
            Assert.AreEqual(preflight ? 0 : 1, f.Snapshot.Attempts);
            Assert.HasCount(1, Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
            StringAssert.Contains(f.Output.Text, "attempt-1");
            Wait(() => f.Text("RunStateText").Contains("已中止", StringComparison.Ordinal));
            Assert.DoesNotContain("已耗尽", f.Text("RunStateText"));
        }
    }

    internal static void FaultOwnershipAndFailedExitRemainStoppable()
    {
        using var f = new Fixture();
        f.Start();
        f.Latest.StopFailure = true;
        f.Latest.Fault();
        Wait(() => f.Snapshot.State == CommandRecoveryState.OwnershipUnconfirmed);
        Wait(() => f.Text("RunStateText").Contains("归属未确认", StringComparison.Ordinal));
        Assert.IsFalse(f.Button("RunButton").IsEnabled);
        Assert.IsFalse(f.Button("RestartButton").IsEnabled);
        Assert.IsTrue(f.Button("StopButton").IsEnabled);
        f.Window.Close();
        Wait(() => f.Exit.Failures == 1);
        Assert.IsTrue(f.Window.IsVisible);
        Assert.IsTrue(f.Coordinator.IsBusy(f.CommandId));
        f.Clock.Advance(100);
        Assert.HasCount(1, f.Sessions);
        f.Latest.StopFailure = false;
        f.Button("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(() => !f.Coordinator.IsBusy(f.CommandId));
        f.Start();
        Assert.HasCount(2, f.Sessions);
        Assert.AreEqual(0, f.Snapshot.Attempts);
    }

    internal static void IndependentBatchAndGroupSourcesStaySeparate()
    {
        using var f = new Fixture(commandCount: 2);
        foreach (var command in f.Window.Projects[0].Commands) f.Checked.Add(command.Id);
        f.Button("BatchRunButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(() => f.Sessions.Count == 2);
        foreach (var session in f.Sessions) session.Finish(CommandRunState.Failed, 11);
        Wait(() => f.Coordinator.GetSnapshots().All(snapshot => snapshot.State == CommandRecoveryState.Waiting));
        var group = f.Window.Projects[0].Groups[0];
        var runGroup = (Task)Invoke(f.Window, "RunParallelGroupAsync", f.Window.Projects[0], group)!;
        Wait(() => runGroup.IsCompleted);
        Assert.HasCount(2, f.Sessions);
        Assert.IsTrue(f.Coordinator.GetSnapshots().All(snapshot => snapshot.Attempts == 0));
        foreach (var command in f.Window.Projects[0].Commands) f.Coordinator.StopAsync(command.Id).GetAwaiter().GetResult();
        var grouped = (Task)Invoke(f.Window, "RunParallelGroupAsync", f.Window.Projects[0], group)!;
        Wait(() => grouped.IsCompleted);
        Assert.HasCount(4, f.Sessions);
        Assert.IsTrue(f.Coordinator.GetSnapshots().All(snapshot => snapshot.RoundId is null));
        foreach (var session in f.Sessions.TakeLast(2)) session.Finish(CommandRunState.Succeeded, 0);
        Wait(() => f.Coordinator.GetSnapshots().All(snapshot => !snapshot.IsBusy));
        f.Clock.Advance(100);
        Assert.HasCount(4, f.Sessions);
    }

    internal static void EditorPreviewAndClonesPreservePolicyWithoutRunning()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var f = new Fixture(probePort: ((IPEndPoint)listener.LocalEndpoint).Port);
        var command = f.Window.Projects[0].Commands[0];
        var editor = new CommandEditorWindow(f.WorkingDirectory, command);
        try
        {
            Assert.IsTrue(((CheckBox)editor.FindName("AutoRestartEnabledBox")).IsChecked);
            ((ComboBox)editor.FindName("KindBox")).SelectedIndex = 0;
            Assert.IsFalse(((CheckBox)editor.FindName("AutoRestartEnabledBox")).IsChecked);
            Assert.IsFalse(((CheckBox)editor.FindName("AutoRestartEnabledBox")).IsEnabled);
            Assert.IsTrue(command.AutoRestartEnabled);
            editor.Dispatcher.BeginInvoke(new Action(() => Invoke(editor, "SaveCommand")));
            Assert.IsTrue(editor.ShowDialog());
            Assert.IsFalse(editor.Result!.AutoRestartEnabled);
        }
        finally { if (editor.IsVisible) editor.Close(); }
        var clone = (CommandDefinition)typeof(MainWindow).GetMethod("CloneCommand", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [command])!;
        Assert.IsTrue(clone.AutoRestartEnabled);
        clone.AutoRestartEnabled = false;
        Assert.IsTrue(command.AutoRestartEnabled);
        var preview = (string)Invoke(f.Window, "BuildImportPreview", new WorkspaceConfiguration { Projects = f.Window.Projects.ToList() })!;
        StringAssert.Contains(preview, "最多追加 3 次");
        StringAssert.Contains(preview, "2、4、8 秒");
        StringAssert.Contains(preview, "仅独立运行生效，分组运行不生效");
        f.Clock.Advance(100);
        Assert.IsEmpty(f.Sessions);
        Assert.IsFalse(listener.Pending(), "编辑、复制和预览不能连接探测端口。");
        Assert.IsEmpty(Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
    }

    internal static void OwnSavePausesAndKeepsOriginalBudget()
    {
        using var f = new Fixture();
        f.Start(); f.Latest.Finish(CommandRunState.Failed, 7);
        Wait(() => f.Snapshot.State == CommandRecoveryState.Waiting);
        Assert.IsTrue((bool)Invoke(f.Window, "BeginWorkspaceChange")!);
        f.Clock.Advance(20);
        var save = (Task<bool>)Invoke(f.Window, "SaveWorkspaceAsync")!;
        Wait(() => save.IsCompleted);
        Assert.IsTrue(save.Result);
        Assert.HasCount(1, f.Sessions);
        Invoke(f.Window, "EndWorkspaceChange");
        Wait(() => f.Sessions.Count == 2);
        Assert.AreEqual(1, f.Snapshot.Attempts);
        Assert.IsTrue(Field<bool>(f.Window, "_canSave"));
    }

    internal static void CreationRollbackFaultDoesNotLeaveFalseBatch()
    {
        using var f = new Fixture();
        var ownership = new FakeSession();
        ownership.Fault();
        f.CreateFailure = new CommandCreationException(ownership, new IOException("synthetic creation rollback"));
        var start = f.StartTask(); Wait(() => start.IsCompleted);
        Assert.IsNull(start.Result.Session);
        Assert.AreEqual(CommandRecoveryState.OwnershipUnconfirmed, f.Snapshot.State);
        Assert.IsEmpty(Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
        Wait(() => !f.Button("RestartButton").IsEnabled && f.Button("StopButton").IsEnabled);
        f.Button("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(() => !f.Coordinator.IsBusy(f.CommandId));
        Assert.IsTrue(ownership.IsOwnershipReleased);
    }

    internal static void ImportMergeAndFailedEditorSavePreservePolicy()
    {
        using var f = new Fixture();
        var local = f.Window.Projects[0];
        var imported = (ProjectDefinition)typeof(MainWindow).GetMethod("CloneProject", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [local])!;
        imported.Groups.Clear(); imported.Commands[0].AutoRestartEnabled = false;
        var configuration = new WorkspaceConfiguration { Projects = [imported] };
        foreach (var choice in new[] { "PrimaryActionButton", "NegativeActionButton", "CancelActionButton" })
        {
            var target = new List<ProjectDefinition> { (ProjectDefinition)typeof(MainWindow).GetMethod("CloneProject", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [local])! };
            var merged = Respond(() => (bool)Invoke(f.Window, "MergeImportedWorkspace", configuration, target)!, window =>
            {
                Assert.IsInstanceOfType<AppMessageDialogWindow>(window);
                ((Button)window.FindName(choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.AreEqual(choice != "CancelActionButton", merged);
            Assert.AreEqual(choice != "PrimaryActionButton", target[0].Commands[0].AutoRestartEnabled);
            Assert.IsTrue(local.Commands[0].AutoRestartEnabled);
        }
        var before = File.ReadAllBytes(f.Store.FilePath);
        f.FailSave = true;
        var editing = Respond(() => (Task)Invoke(f.Window, "EditSelectedCommandAsync")!, window =>
        {
            if (window is CommandEditorWindow editor)
            {
                ((CheckBox)editor.FindName("AutoRestartEnabledBox")).IsChecked = false;
                Invoke(editor, "SaveCommand");
            }
            else ((Button)window.FindName("PrimaryActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Respond(() => { Wait(() => editing.IsCompleted); return true; }, window =>
            ((Button)window.FindName("PrimaryActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        editing.GetAwaiter().GetResult();
        Assert.IsTrue(f.Window.Projects[0].Commands[0].AutoRestartEnabled);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Store.FilePath));
        Assert.IsEmpty(f.Sessions);
        Assert.IsEmpty(Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
    }

    internal static void EditorTargetSurvivesNestedWorkspaceCallbacks(bool replaceConfiguration)
    {
        using var f = new Fixture(commandCount: 2);
        var project = f.Window.Projects[0];
        var command = project.Commands[0];
        var before = File.ReadAllBytes(f.Store.FilePath);
        Stage = replaceConfiguration ? "编辑期间替换配置使草稿失效" : "编辑弹窗中重载与更换选择";
        var editing = Respond(() => (Task)Invoke(f.Window, "EditSelectedCommandAsync")!, window =>
        {
            if (window is CommandEditorWindow editor)
            {
                if (replaceConfiguration)
                {
                    var replacement = (ProjectDefinition)typeof(MainWindow).GetMethod("CloneProject", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [project])!;
                    Invoke(f.Window, "ReplaceProjects", (object)new[] { replacement });
                }
                else
                {
                    // 同步命中模态消息循环中的重载入口，不依赖 250 ms 计时碰撞。
                    var reload = f.Window.ReloadWorkspaceForSecondaryLaunchAsync();
                    Wait(() => reload.IsCompleted);
                    reload.GetAwaiter().GetResult();
                    Assert.AreSame(project, f.Window.Projects[0], "弹窗仍打开时不能替换其项目对象。");
                    Assert.AreSame(command, f.Window.Projects[0].Commands[0]);
                    ((ListBox)f.Window.FindName("CommandList")).SelectedIndex = 1;
                }
                ((CheckBox)editor.FindName("AutoRestartEnabledBox")).IsChecked = false;
                Invoke(editor, "SaveCommand");
            }
            else ((Button)window.FindName("PrimaryActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Respond(() => { Wait(() => editing.IsCompleted); return true; }, window =>
            ((Button)window.FindName("PrimaryActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        editing.GetAwaiter().GetResult();
        if (replaceConfiguration)
        {
            Assert.IsTrue(f.Window.Projects[0].Commands.All(item => item.AutoRestartEnabled));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Store.FilePath));
        }
        else
        {
            Assert.IsFalse(f.Window.Projects[0].Commands[0].AutoRestartEnabled);
            Assert.IsTrue(f.Window.Projects[0].Commands[1].AutoRestartEnabled, "改变当前选择不能把原草稿保存到另一个命令。");
            var saved = f.Store.LoadAsync().GetAwaiter().GetResult();
            Assert.IsFalse(saved.Projects[0].Commands[0].AutoRestartEnabled);
            Assert.IsTrue(saved.Projects[0].Commands[1].AutoRestartEnabled);
        }
        Assert.IsEmpty(f.Sessions);
        Assert.IsEmpty(Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
    }

    private static T Respond<T>(Func<T> action, Action<Window> respond)
    {
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().LastOrDefault(window => window.IsVisible &&
                window is AppMessageDialogWindow or CommandEditorWindow && !handled.Contains(window));
            if (dialog is null) return;
            handled.Add(dialog);
            try { respond(dialog); }
            catch (Exception exception) { failure = exception; dialog.Close(); }
        };
        timer.Start();
        try
        {
            var result = action();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
        finally { timer.Stop(); }
    }

    internal static void RealProcessRetriesUseNewProbeAndRedactedLogBatches()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var f = new Fixture(real: true, probePort: port);
        f.Start();
        var seen = new HashSet<ICommandRunSession>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Wait(() => f.Snapshot.Session is CommandRunSession { TcpProbe.State: TcpProbeState.Passed });
            var session = f.Snapshot.Session!;
            Assert.IsTrue(seen.Add(session));
            Wait(() => f.Output.Text.Contains("[已隐藏]", StringComparison.Ordinal));
            Wait(() => f.Text("OverviewProbeCountText").Contains("1 个探测通过", StringComparison.Ordinal));
            StringAssert.Contains(f.Text("OverviewProbeCountText"), "0 个待重试");
            Assert.AreEqual(attempt, f.Snapshot.Attempts);
            File.WriteAllText(Path.Combine(f.WorkingDirectory, "fail.marker"), "synthetic");
            Wait(() => f.Snapshot.State == (attempt == 3 ? CommandRecoveryState.Exhausted : CommandRecoveryState.Waiting));
            Assert.AreEqual(19, session.Completion.Result.ExitCode);
            Assert.IsTrue(session.IsOwnershipReleased);
            File.Delete(Path.Combine(f.WorkingDirectory, "fail.marker"));
            if (attempt < 3) f.Clock.Advance(2 << attempt);
        }
        Assert.HasCount(4, Directory.GetFiles(f.Root, "run.json", SearchOption.AllDirectories));
        foreach (var path in Directory.GetFiles(f.Root, "*.log", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("synthetic-app-secret", text);
            Assert.DoesNotContain("自动重启", text);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "TalosDesk.RecoveryDesktop", Guid.NewGuid().ToString("N"));
        internal string WorkingDirectory { get; }
        internal WorkspaceStore Store { get; }
        internal ManualTime Clock { get; } = new();
        internal ExitInteraction Exit { get; } = new();
        internal MainWindow Window { get; }
        internal ConcurrentQueue<FakeSession> Sessions { get; } = new();
        internal FakeSession Latest => Sessions.Last();
        internal CommandRecoveryCoordinator Coordinator => Field<CommandRecoveryCoordinator>(Window, "_recovery");
        internal Guid CommandId => Window.Projects[0].Commands[0].Id;
        internal CommandRecoverySnapshot Snapshot => Coordinator.GetSnapshot(CommandId)!;
        internal HashSet<Guid> Checked => Field<HashSet<Guid>>(Window, "_checkedCommandIds");
        internal TextBox Output => (TextBox)Window.FindName("OutputTextBox");
        internal Exception? CreateFailure;
        internal bool FailSave;
        internal Fixture(bool real = false, int commandCount = 1, int? probePort = null)
        {
            Stage = "初始化隔离工作区";
            Directory.CreateDirectory(Root);
            WorkingDirectory = Path.Combine(Root, "commands"); Directory.CreateDirectory(WorkingDirectory);
            Store = new(Path.Combine(Root, "workspace.json"), () => { if (FailSave) throw new IOException("synthetic save failure"); }, null);
            var commands = Enumerable.Range(0, commandCount).Select(index => new CommandDefinition
            {
                Name = $"合成服务 {index}", WorkingDirectory = WorkingDirectory, Kind = CommandKind.Service, AutoRestartEnabled = true,
                Command = "Write-Output $env:SYNTHETIC_SECRET; Write-Output ('pid:' + $PID); while (-not (Test-Path './fail.marker')) { Start-Sleep -Milliseconds 20 }; exit 19",
                TcpProbe = probePort is { } port ? new() { Port = port, IntervalSeconds = 1 } : null,
                EnvironmentVariables = [new() { Name = "SYNTHETIC_SECRET", IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect("synthetic-app-secret") }]
            }).ToList();
            Store.SaveAsync(new() { Projects = [new() { Name = "隔离恢复项目", Directory = WorkingDirectory, Commands = commands,
                Groups = [new() { Name = "服务组", CommandIds = commands.Select(command => command.Id).ToList() }] }] }).GetAwaiter().GetResult();
            Window = new MainWindow(Store, "隔离恢复验收", null, null, null, trayIcon: new TestTrayIcon(), exitInteraction: Exit,
                recoveryTime: Clock, createSession: real ? null : (_, plan) =>
                {
                    if (!Directory.Exists(plan.WorkingDirectory)) throw new DirectoryNotFoundException();
                    if (CreateFailure is { } failure) throw failure;
                    var session = new FakeSession(); Sessions.Enqueue(session);
                    plan.OutputReceived?.Invoke(null, new CommandOutput(DateTimeOffset.Now, "stdout", $"attempt-{Sessions.Count}"));
                    return session;
                });
            Window.Show();
            Wait(() => Button("ManualUpdateCheckButton").IsEnabled);
            ((ListBox)Window.FindName("ProjectList")).SelectedIndex = 0;
            Button("CommandsPageButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            ((ListBox)Window.FindName("CommandList")).SelectedIndex = 0;
        }
        internal Button Button(string name) => (Button)Window.FindName(name);
        internal string Text(string name) => ((TextBlock)Window.FindName(name)).Text;
        internal Task<(ICommandRunSession? Session, string Error)> StartTask(bool restart = false) =>
            (Task<(ICommandRunSession?, string)>)Invoke(Window, "StartCommandSessionAsync", Window.Projects[0], Window.Projects[0].Commands[0],
                CommandStartSource.Independent, false, restart, CancellationToken.None)!;
        internal void Start(bool restart = false)
        {
            var task = StartTask(restart);
            Wait(() => task.IsCompleted);
            Assert.IsNotNull(task.GetAwaiter().GetResult().Session, task.Result.Error);
        }
        public void Dispose()
        {
            Exit.Confirm = (_, _) => true;
            foreach (var session in Sessions) session.StopFailure = false;
            if (Window.IsVisible) Window.Close();
            Wait(() => !Application.Current.Windows.Cast<Window>().Contains(Window));
            Directory.Delete(Root, true);
        }
    }

    private sealed class ExitInteraction : IMainWindowExitInteraction
    {
        internal Func<int, int, bool> Confirm = (_, _) => true;
        internal int ConfirmCount, Failures;
        public bool ConfirmExit(Window owner, int commandCount, int sequenceCount, int pendingCount = 0)
        { ConfirmCount++; return Confirm(commandCount, pendingCount); }
        public Task StopAsync(CommandRunSession session) => session.StopAsync();
        public void ReportFailure(Window owner, Exception exception) => Failures++;
    }

    private sealed class FakeSession : ICommandRunSession
    {
        private readonly TaskCompletionSource<CommandRunResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CommandRunResult> Completion => _result.Task;
        public bool IsOwnershipReleased { get; private set; }
        public bool IsStopRequested { get; private set; }
        internal bool StopFailure;
        internal void Finish(CommandRunState state, int code) { IsOwnershipReleased = true; _result.TrySetResult(new(state, code, false)); }
        internal void Fault() => _result.TrySetException(new IOException("synthetic observation failure"));
        public Task<CommandStopResult> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsStopRequested = true;
            if (StopFailure) throw new IOException("synthetic stop failure");
            IsOwnershipReleased = true; Finish(CommandRunState.Stopped, 1);
            return Task.FromResult(CommandStopResult.StoppedAfterCtrlC);
        }
        public async ValueTask DisposeAsync() => await StopAsync();
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Wait(Func<bool> condition, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { Assert.IsTrue(DateTime.UtcNow < deadline, $"恢复桌面回归超时：{Stage}；{detail?.Invoke()}"); Pump(); }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("TALOSDESK_LAYOUT_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    internal sealed class ManualTime : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<Timer> _timers = [];
        private long _ticks;
        private int _wallOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp()).AddYears(_wallOffset);
        internal void JumpWallClock() => _wallOffset = _wallOffset == 0 ? 20 : 0;
        internal int Pending { get { lock (_sync) return _timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new Timer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(int seconds)
        {
            Timer[] due;
            lock (_sync)
            {
                _ticks += TimeSpan.FromSeconds(seconds).Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due) timer.Due = long.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            internal TimerCallback Callback => callback;
            internal object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return true;
            }
            public void Dispose() { lock (owner._sync) { Due = long.MaxValue; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
