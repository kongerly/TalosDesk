using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class TcpProbeRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.ProbeDesktop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var command = new CommandDefinition
        {
            Name = "合成 TCP 服务", WorkingDirectory = root, Kind = CommandKind.Service,
            Command = "1..300 | ForEach-Object { Write-Output ('probe-desktop-output-{0}' -f $_) }; Write-Output 'probe-output-complete'; while ($true) { Start-Sleep -Milliseconds 100 }",
            TcpProbe = new() { Port = port, IntervalSeconds = 1, StartupTimeoutSeconds = 1, FailureThreshold = 2 }
        };
        var project = new ProjectDefinition
        {
            Name = "探测隔离项目", Directory = root, Commands = [command],
            Groups = [new() { Name = "分组一", CommandIds = [command.Id] }, new() { Name = "分组二", CommandIds = [command.Id] }]
        };
        var configuration = new WorkspaceConfiguration
        {
            Projects = [project, new() { Name = "另一个项目", Directory = Path.Combine(root, "second") }]
        };
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        store.SaveAsync(configuration).GetAwaiter().GetResult();
        var window = new MainWindow(store, "隔离 TCP 验收", null, null, null, exitInteraction: new ExitInteraction());
        CommandEditorWindow? editor = null;
        DesktopTestHost.RunWithCleanup(() =>
        {
            Stage = "编辑与导入预览";
            editor = new CommandEditorWindow(root, command);
            Assert.IsTrue(((CheckBox)editor.FindName("ProbeEnabledBox")).IsChecked);
            ((TextBox)editor.FindName("ProbePortBox")).Text = "12346";
            ((ComboBox)editor.FindName("KindBox")).SelectedIndex = 0;
            Assert.IsFalse(((CheckBox)editor.FindName("ProbeEnabledBox")).IsEnabled);
            Assert.AreEqual(Visibility.Visible, ((TextBlock)editor.FindName("ProbeTaskHint")).Visibility);
            ((ComboBox)editor.FindName("KindBox")).SelectedIndex = 1;
            editor.Dispatcher.BeginInvoke(new Action(() => Invoke(editor, "SaveCommand")));
            Assert.IsTrue(editor.ShowDialog());
            Assert.AreEqual(12346, editor.Result!.TcpProbe!.Port);
            Assert.AreEqual(port, command.TcpProbe.Port);
            var preview = (string)Invoke(window, "BuildImportPreview", configuration)!;
            StringAssert.Contains(preview, $"]:{port}");
            StringAssert.Contains(preview, "间隔 1 秒");
            StringAssert.Contains(preview, "不会自动运行命令或连接探测端口");
            Assert.IsFalse(listener.Pending());
            var clone = (CommandDefinition)typeof(MainWindow).GetMethod("CloneCommand", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [command])!;
            clone.TcpProbe!.Port = 12347;
            Assert.AreEqual(port, command.TcpProbe.Port);

            window.Show();
            Wait(() => Find<Button>(window, "ManualUpdateCheckButton").IsEnabled);
            Find<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "CommandsPageButton");
            Find<ListBox>(window, "CommandList").SelectedIndex = 0;
            Click(window, "RunButton");
            Stage = "运行与项目去重汇总";
            Wait(() => Status(window).Contains("TCP 探测通过"));
            Assert.AreEqual("1", Find<TextBlock>(window, "OverviewRunningCountText").Text);
            Assert.AreEqual("其中 1 个探测通过", Find<TextBlock>(window, "OverviewProbeCountText").Text);
            Click(window, "OutputPageButton");
            var output = Find<TextBox>(window, "OutputTextBox");
            Wait(() => output.Text.Contains("probe-output-complete"));
            output.Select(0, 5);
            var selected = output.SelectedText;
            output.UpdateLayout();
            output.ScrollToVerticalOffset(100);
            Wait(() => Math.Abs(output.VerticalOffset - 100) < 0.1);
            var verticalOffset = output.VerticalOffset;
            var originalOutput = output.Text;
            listener.Stop();
            Stage = "运行中失联且不改变长日志、选择和滚动位置";
            Wait(() => Status(window).Contains("连续失败 1/2"));
            Assert.AreEqual("其中 1 个探测通过", Find<TextBlock>(window, "OverviewProbeCountText").Text);
            Wait(() => Status(window).Contains("TCP 不可达"));
            Assert.AreEqual(selected, output.SelectedText);
            Assert.AreEqual(originalOutput, output.Text);
            Assert.AreEqual(verticalOffset, output.VerticalOffset, 0.1);
            Assert.AreEqual("1", Find<TextBlock>(window, "OverviewRunningCountText").Text);
            Assert.AreEqual("其中 0 个探测通过", Find<TextBlock>(window, "OverviewProbeCountText").Text);

            Stage = "重启超时与恢复";
            Click(window, "RestartButton");
            Wait(() => Status(window).Contains("启动等待超时"));
            using var recovered = new TcpListener(IPAddress.Loopback, port);
            recovered.Start();
            Wait(() => Status(window).Contains("TCP 探测通过"));
            var history = Find<ComboBox>(window, "RunHistoryComboBox");
            Assert.IsGreaterThan(1, history.Items.Count);
            history.SelectedIndex = 1;
            var historicalStatus = Find<TextBlock>(window, "OutputRunStateText").Text;
            Assert.DoesNotContain("TCP", historicalStatus);
            recovered.Stop();
            Stage = "历史结果不受当前探测影响";
            Wait(() => Status(window).Contains("TCP 不可达"));
            Assert.AreEqual(historicalStatus, Find<TextBlock>(window, "OutputRunStateText").Text);
            Assert.AreEqual(Visibility.Collapsed, Find<TextBlock>(window, "ProbeBoundaryText").Visibility);

            Stage = "跨项目切换与停止";
            Find<ListBox>(window, "ProjectList").SelectedIndex = 1;
            Assert.AreEqual("0", Find<TextBlock>(window, "OverviewRunningCountText").Text);
            Find<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Find<ListBox>(window, "CommandList").SelectedIndex = 0;
            Assert.AreEqual("1", Find<TextBlock>(window, "OverviewRunningCountText").Text);
            Click(window, "StopButton");
            Wait(() => Find<TextBlock>(window, "OverviewRunningCountText").Text == "0");
            Assert.AreEqual("其中 0 个探测通过", Find<TextBlock>(window, "OverviewProbeCountText").Text);
            var diskLog = Directory.GetFiles(store.FilePath + ".logs", "stdout.log", SearchOption.AllDirectories).Select(File.ReadAllText);
            Assert.IsTrue(diskLog.All(text => !text.Contains("TCP")));
        }, () =>
        {
            editor?.Close();
            window.Close();
            Wait(() => !window.IsVisible);
            Directory.Delete(root, true);
            System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        });
    }

    private static T Find<T>(Window window, string name) => (T)window.FindName(name);
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Status(Window window) => Find<TextBlock>(window, "RunStateText").Text;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"TCP 桌面检查超时：{Stage}");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class ExitInteraction : IMainWindowExitInteraction
    {
        public bool ConfirmExit(Window owner, int commandCount, int sequenceCount) => true;
        public Task StopAsync(CommandRunSession session) => session.StopAsync();
        public void ReportFailure(Window owner, Exception exception) => Assert.Fail(exception.ToString());
    }
}
