using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class TrayCommandRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.TrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        var service = new CommandDefinition
        {
            Name = "隔离托盘服务", WorkingDirectory = root, Kind = CommandKind.Service,
            Command = "$i = 0; while ($true) { Write-Output ('tray-heartbeat-' + $i); [Console]::Error.WriteLine('tray-stderr-' + $i); $i++; Start-Sleep -Milliseconds 100 }"
        };
        store.SaveAsync(new WorkspaceConfiguration
        {
            Projects = [new ProjectDefinition { Name = "隔离托盘项目", Directory = root, Commands = [service] }]
        }).GetAwaiter().GetResult();
        var icon = new TestTrayIcon();
        var interaction = new ExitInteraction();
        var window = new MainWindow(store, "隔离托盘测试", null, null, null,
            trayIcon: icon, exitInteraction: interaction);
        DesktopTestHost.RunWithCleanup(() =>
        {
            window.Show();
            WaitUntil(() => ((Button)window.FindName("ManualUpdateCheckButton")).IsEnabled);
            ((ListBox)window.FindName("ProjectList")).SelectedIndex = 0;
            ((Button)window.FindName("CommandsPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((ListBox)window.FindName("CommandList")).SelectedIndex = 0;
            ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var output = (TextBox)window.FindName("OutputTextBox");
            WaitUntil(() => output.Text.Contains("tray-heartbeat-0", StringComparison.Ordinal));
            Stage = "隐藏期间命令与日志持续运行";
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            Assert.IsFalse(window.IsVisible);
            WaitUntil(() => output.Text.Contains("tray-heartbeat-5", StringComparison.Ordinal));
            var stdout = Directory.GetFiles(store.FilePath + ".logs", "stdout.log", SearchOption.AllDirectories).Single();
            WaitUntil(() => ReadLiveLog(stdout).Contains("tray-heartbeat-5", StringComparison.Ordinal));
            var stderr = Directory.GetFiles(store.FilePath + ".logs", "stderr.log", SearchOption.AllDirectories).Single();
            WaitUntil(() => ReadLiveLog(stderr).Contains("tray-stderr-5", StringComparison.Ordinal));
            Assert.IsFalse(window.IsVisible);
            Assert.IsTrue(((Button)window.FindName("StopButton")).IsEnabled);

            Stage = "同一工作区重复启动唤醒最大化窗口";
            using (var primary = new WorkspaceInstanceCoordinator(store.FilePath))
            {
                Assert.IsTrue(primary.IsPrimary);
                primary.StartListening(() => window.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    window.RestoreWindow();
                    await window.ReloadWorkspaceForSecondaryLaunchAsync();
                })));
                Task.Run(() =>
                {
                    using var secondary = new WorkspaceInstanceCoordinator(store.FilePath);
                    Assert.IsFalse(secondary.IsPrimary);
                    secondary.SignalPrimaryInstance();
                }).GetAwaiter().GetResult();
                WaitUntil(() => window.IsVisible);
                Assert.AreEqual(WindowState.Maximized, window.WindowState);
                StringAssert.Contains(((TextBlock)window.FindName("SaveStatusText")).Text, "已有任务运行");
            }

            Stage = "取消退出保留运行命令和窗口";
            interaction.Confirm = false;
            window.WindowState = WindowState.Minimized;
            icon.Exit();
            WaitUntil(() => interaction.ConfirmCount == 1);
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(1, interaction.ConfirmCount);
            Assert.AreEqual(0, interaction.StopCount);
            Assert.AreEqual(0, icon.DisposeCount);
            Assert.IsTrue(((Button)window.FindName("StopButton")).IsEnabled);
            var before = output.Text;
            WaitUntil(() => output.Text != before);

            Stage = "停止失败恢复窗口并允许重试";
            interaction.Confirm = true;
            interaction.FailStop = true;
            window.WindowState = WindowState.Minimized;
            icon.Exit();
            WaitUntil(() => interaction.FailureCount == 1);
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            Assert.AreEqual(0, icon.DisposeCount);
            Assert.IsFalse(interaction.Session!.Completion.IsCompleted);
            Assert.IsTrue(((Button)window.FindName("StopButton")).IsEnabled);
            before = output.Text;
            WaitUntil(() => output.Text != before);

            Stage = "确认退出与停止期间重复退出";
            interaction.FailStop = false;
            interaction.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.WindowState = WindowState.Minimized;
            icon.Exit();
            WaitUntil(() => interaction.StopCount == 2);
            Assert.AreEqual(3, interaction.ConfirmCount);
            Assert.AreEqual(2, interaction.StopCount);
            icon.Exit();
            window.Close();
            Assert.AreEqual(3, interaction.ConfirmCount);
            Assert.AreEqual(2, interaction.StopCount);
            Assert.AreEqual(0, icon.DisposeCount);
            interaction.Gate.SetResult();
            WaitUntil(() => icon.DisposeCount == 1);
            Assert.IsTrue(interaction.Session.Completion.IsCompleted);
            StringAssert.Contains(File.ReadAllText(stdout), "tray-heartbeat-5");
            Assert.IsFalse(window.IsVisible);
        }, () =>
        {
            interaction.Confirm = true;
            interaction.FailStop = false;
            interaction.Gate?.TrySetResult();
            if (icon.DisposeCount == 0)
            {
                window.Close();
                WaitUntil(() => icon.DisposeCount == 1);
            }
            Directory.Delete(root, true);
        });
    }

    private static string ReadLiveLog(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"托盘检查超时：{Stage}。");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class ExitInteraction : IMainWindowExitInteraction
    {
        internal bool Confirm = true;
        internal bool FailStop;
        internal int ConfirmCount, StopCount, FailureCount;
        internal TaskCompletionSource? Gate;
        internal CommandRunSession? Session;

        public bool ConfirmExit(Window owner, int commandCount, int sequenceCount)
        {
            Assert.IsTrue(owner.IsVisible);
            Assert.AreEqual(1, commandCount);
            Assert.AreEqual(0, sequenceCount);
            ConfirmCount++;
            return Confirm;
        }

        public async Task StopAsync(CommandRunSession session)
        {
            StopCount++;
            Session = session;
            if (FailStop) throw new IOException("合成停止失败");
            if (Gate is not null) await Gate.Task;
            await session.StopAsync();
        }

        public void ReportFailure(Window owner, Exception exception)
        {
            Assert.IsTrue(owner.IsVisible);
            Assert.AreEqual("合成停止失败", exception.Message);
            FailureCount++;
        }
    }
}
