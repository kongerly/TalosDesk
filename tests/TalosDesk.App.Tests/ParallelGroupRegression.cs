using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class ParallelGroupRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        CheckFailure(serviceOnly: true, RetryMode.None);
        CheckFailure(serviceOnly: false, RetryMode.None);
        CheckFailure(serviceOnly: false, RetryMode.ManualRestart);
        CheckFailure(serviceOnly: false, RetryMode.GroupRerun);
    }

    private static void CheckFailure(bool serviceOnly, RetryMode retry)
    {
        Stage = $"{(serviceOnly ? "纯服务" : "混合")}分组 / {retry} / 启动";
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.ParallelGroupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failedService = new CommandDefinition
        {
            Name = "稍后失败服务", Kind = CommandKind.Service, WorkingDirectory = root,
            Command = "Write-Output 'failure-service-ready'; while (-not (Test-Path './fail.marker')) { Start-Sleep -Milliseconds 20 }; exit 13"
        };
        var peer = new CommandDefinition
        {
            Name = "同组持续服务", Kind = CommandKind.Service, WorkingDirectory = root,
            Command = "Write-Output 'peer-service-ready'; while ($true) { Start-Sleep -Milliseconds 100 }"
        };
        var skipped = new CommandDefinition
        {
            Name = "手动启动并跳过的服务", Kind = CommandKind.Service, WorkingDirectory = root,
            Command = "Write-Output 'skipped-service-ready'; while ($true) { Start-Sleep -Milliseconds 100 }"
        };
        var task = new CommandDefinition { Name = "短任务", WorkingDirectory = root, Command = "Write-Output 'task-success'" };
        var group = new CommandGroupDefinition
        {
            Name = "隔离同时分组", ExecutionMode = CommandGroupExecutionMode.Parallel,
            CommandIds = serviceOnly ? [failedService.Id, peer.Id, skipped.Id] : [failedService.Id, peer.Id, skipped.Id, task.Id]
        };
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        store.SaveAsync(new() { Projects = [new() { Name = "隔离分组项目", Directory = root, Commands = [failedService, peer, skipped, task], Groups = [group] }] })
            .GetAwaiter().GetResult();
        var window = new MainWindow(store, "隔离分组验收", null, null, null,
            trayIcon: new TestTrayIcon(), exitInteraction: new ExitInteraction());
        var dialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        DesktopTestHost.RunWithCleanup(() =>
        {
            window.Show();
            Wait(() => ((Button)window.FindName("ManualUpdateCheckButton")).IsEnabled);
            ((ListBox)window.FindName("ProjectList")).SelectedIndex = 0;
            var project = window.Projects[0];
            group = project.Groups[0];
            skipped = project.Commands[2];
            peer = project.Commands[1];
            Assert.IsTrue((bool)Invoke(window, "TryStartCommand", project, skipped, null, string.Empty, false)!);
            var skippedSession = Sessions(window)[skipped.Id];
            Invoke(window, "RunParallelGroup", project, group);
            var failingSession = Sessions(window)[failedService.Id];
            var originalPeer = Sessions(window)[peer.Id];
            Wait(() => failingSession.GetRecentOutput().Any(output => output.Text == "failure-service-ready") &&
                originalPeer.GetRecentOutput().Any(output => output.Text == "peer-service-ready"));
            if (!serviceOnly)
            {
                Wait(() => window.GroupItems[0].StatusText == "任务已成功 · 2 个服务继续运行");
                Assert.IsFalse(failingSession.Completion.IsCompleted);
                Assert.IsFalse(originalPeer.Completion.IsCompleted);
            }

            CommandRunSession? replacement = null;
            if (retry != RetryMode.None)
            {
                Stage = $"混合分组 / {retry} / 建立新会话";
                var stop = originalPeer.StopAsync();
                Wait(() => stop.IsCompleted && originalPeer.Completion.IsCompleted);
                stop.GetAwaiter().GetResult();
                if (retry == RetryMode.ManualRestart)
                    Assert.IsTrue((bool)Invoke(window, "TryStartCommand", project, peer, null, string.Empty, false)!);
                else
                    Invoke(window, "RunParallelGroup", project, group);
                replacement = Sessions(window)[peer.Id];
                Assert.AreNotSame(originalPeer, replacement);
                Wait(() => replacement.GetRecentOutput().Any(output => output.Text == "peer-service-ready"));
                if (retry == RetryMode.GroupRerun)
                    Wait(() => window.GroupItems[0].StatusText == "任务已成功 · 1 个服务继续运行");
            }

            Stage = $"{(serviceOnly ? "纯服务" : "混合")}分组 / {retry} / 失败清理与提示";
            var dialogCount = 0;
            Exception? dialogFailure = null;
            dialogTimer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<AppMessageDialogWindow>()
                    .FirstOrDefault(candidate => candidate.IsVisible && candidate.Owner == window);
                if (dialog is null) return;
                try
                {
                    Assert.AreEqual("TalosDesk · 分组执行失败", dialog.Title);
                    var message = ((TextBox)dialog.FindName("MessageBox")).Text;
                    StringAssert.Contains(message, "稍后失败服务");
                    StringAssert.Contains(message, "退出码为 13");
                    StringAssert.Contains(message, "已停止此分组启动的其他运行中命令");
                    Assert.AreEqual(CommandRunState.Failed, failingSession.Completion.Result.State);
                    Assert.IsTrue(originalPeer.Completion.IsCompleted);
                    Assert.AreEqual(CommandRunState.Stopped, originalPeer.Completion.Result.State);
                    Assert.IsFalse(skippedSession.Completion.IsCompleted, "失败清理不能停止本次跳过的手动服务。");
                    Assert.IsFalse(skippedSession.IsStopRequested);
                    if (replacement is not null)
                    {
                        Assert.IsFalse(replacement.Completion.IsCompleted, "旧执行的清理不能停止后来启动的同一命令。");
                        Assert.IsFalse(replacement.IsStopRequested);
                    }
                    if (retry == RetryMode.GroupRerun)
                        Assert.AreEqual("任务已成功 · 1 个服务继续运行", window.GroupItems[0].StatusText);
                    else
                        StringAssert.Contains(window.GroupItems[0].StatusText, "执行失败 · 稍后失败服务 · 退出码 13");
                    dialogCount++;
                }
                catch (Exception exception) { dialogFailure = exception; }
                finally { dialog.Close(); }
            };
            dialogTimer.Start();
            File.WriteAllText(Path.Combine(root, "fail.marker"), "synthetic failure trigger");
            Wait(() => dialogCount == 1 || dialogFailure is not null);
            if (dialogFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(dialogFailure).Throw();
            Assert.AreEqual(1, dialogCount);
        }, () =>
        {
            dialogTimer.Stop();
            window.Close();
            Wait(() => !Application.Current.Windows.Cast<Window>().Contains(window));
            Directory.Delete(root, true);
        });
    }

    private static Dictionary<Guid, CommandRunSession> Sessions(MainWindow window) =>
        (Dictionary<Guid, CommandRunSession>)typeof(MainWindow).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"同时分组桌面检查超时：{Stage}");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private enum RetryMode { None, ManualRestart, GroupRerun }

    private sealed class ExitInteraction : IMainWindowExitInteraction
    {
        public bool ConfirmExit(Window owner, int commandCount, int sequenceCount) => true;
        public Task StopAsync(CommandRunSession session) => session.StopAsync();
        public void ReportFailure(Window owner, Exception exception) => throw new AssertFailedException("分组回归无法停止归属命令。", exception);
    }
}
