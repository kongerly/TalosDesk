using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Updates;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class MainWindowIntegrationTests
{
    [TestMethod]
    public void UpdateStatesDoNotBlockDesktopCommandAndOutputFlows()
    {
        Exception? failure = null;
        var progress = new ProgressState();
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.InitializeComponent();
                RunRegression(progress);
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(40)),
            $"桌面测试线程没有按时退出；阶段：{progress.Stage}，页面阶段：{progress.Phase}。");
        if (failure is not null) throw new AssertFailedException($"桌面集成检查失败：{failure}");
    }

    private static void RunRegression(ProgressState progress)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "TalosDesk.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var workspace = Path.Combine(sandbox, "workspace.json");
        var service = new CommandDefinition
        {
            Name = "更新并发服务",
            Purpose = "验证更新请求不影响运行和停止",
            Command = "$countFile = '.\\restart-count.txt'; $count = if (Test-Path $countFile) { [int](Get-Content $countFile) } else { 0 }; $count++; Set-Content $countFile $count; Write-Output ('update-regression-started-' + $count); [Console]::Error.WriteLine('update-regression-stderr'); while ($true) { Start-Sleep -Milliseconds 100 }",
            WorkingDirectory = sandbox,
            Kind = CommandKind.Service
        };
        var firstTask = new CommandDefinition
        {
            Name = "分组任务一",
            Command = "Write-Output 'group-one'; [Console]::Error.WriteLine('group-one-stderr')",
            WorkingDirectory = sandbox,
            Kind = CommandKind.Task
        };
        var secondTask = new CommandDefinition
        {
            Name = "分组任务二",
            Command = "Write-Output 'group-two'",
            WorkingDirectory = sandbox,
            Kind = CommandKind.Task
        };
        var configuration = new WorkspaceConfiguration
        {
            Projects =
            [
                new ProjectDefinition
                {
                    Name = "隔离更新回归",
                    Directory = sandbox,
                    Commands = [service, firstTask, secondTask],
                    Groups =
                    [
                        new CommandGroupDefinition
                        {
                            Name = "顺序回归",
                            ExecutionMode = CommandGroupExecutionMode.Sequential,
                            CommandIds = [firstTask.Id, secondTask.Id]
                        },
                        new CommandGroupDefinition
                        {
                            Name = "同时回归",
                            ExecutionMode = CommandGroupExecutionMode.Parallel,
                            CommandIds = [firstTask.Id, secondTask.Id]
                        }
                    ]
                }
            ]
        };
        new WorkspaceStore(workspace).SaveAsync(configuration).GetAwaiter().GetResult();

        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var handler = new ScenarioHandler("NetworkFailure");
        try
        {
            using var http = new HttpClient(handler);
            using var client = new GitHubReleaseClient(http, clock);
            using var coordinator = new UpdateCheckCoordinator(
                "0.1.1",
                ReleaseChannel.Preview,
                client,
                new UpdateStateStore(workspace),
                clock);
            var window = new MainWindow(
                new WorkspaceStore(workspace),
                "隔离测试",
                coordinator,
                new ApplicationBuildInfo("0.1.1", "0.1.1", ReleaseChannel.Preview, null),
                new RecordingLauncher());
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            var phase = 0;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            Exception? scenarioFailure = null;
            timer.Tick += (_, _) =>
            {
                try
                {
                    var manual = (Button)window.FindName("ManualUpdateCheckButton");
                    var status = (TextBlock)window.FindName("UpdateStatusText");
                    var commands = (ListBox)window.FindName("CommandList");
                    var output = (TextBox)window.FindName("OutputTextBox");
                    if (DateTime.UtcNow > deadline)
                        throw new AssertFailedException($"等待桌面状态超时，阶段：{phase}，运行状态：{((TextBlock)window.FindName("RunStateText")).Text}，输出：{output.Text}。");

                    if (phase == 0 && manual.IsEnabled)
                    {
                        progress.Stage = "验证无项目页面并恢复隔离项目";
                        Assert.AreEqual(0, handler.RequestCount);
                        Assert.HasCount(1, window.Projects);
                        var loadedProject = window.Projects[0];
                        window.Projects.Clear();
                        Assert.HasCount(0, window.Projects);
                        Assert.IsFalse(File.Exists(workspace + ".update-settings.json"));
                        Assert.IsFalse(File.Exists(workspace + ".update-cache.json"));
                        ((Button)window.FindName("AboutPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual(Visibility.Visible, ((Grid)window.FindName("AboutPage")).Visibility);
                        Assert.AreEqual(1050d, window.MinWidth);
                        Assert.AreEqual(650d, window.MinHeight);
                        var diagnosticsEnabled = (CheckBox)window.FindName("DiagnosticEnabledCheckBox");
                        Assert.IsTrue(diagnosticsEnabled.IsChecked);
                        Assert.IsTrue(diagnosticsEnabled.IsEnabled);
                        Assert.AreEqual("0 条 · 0 B", ((TextBlock)window.FindName("DiagnosticSummaryText")).Text);
                        Assert.IsFalse(Directory.Exists(workspace + ".crashes"));
                        diagnosticsEnabled.IsChecked = false;
                        diagnosticsEnabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.IsFalse(new TalosDesk.Core.Diagnostics.DiagnosticSettingsStore(workspace).Load().Settings.IsEnabled);
                        diagnosticsEnabled.IsChecked = true;
                        diagnosticsEnabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.IsTrue(new TalosDesk.Core.Diagnostics.DiagnosticSettingsStore(workspace).Load().Settings.IsEnabled);
                        window.Projects.Add(loadedProject);
                        ((ListBox)window.FindName("ProjectList")).SelectedIndex = 0;
                        Assert.HasCount(1, window.Projects);
                        progress.Stage = "发起网络失败检查";
                        manual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 1;
                    }
                    else if (phase == 1 && status.Text == "无法连接更新服务")
                    {
                        progress.Stage = "网络失败后运行任务";
                        Assert.AreEqual(1, handler.RequestCount);
                        ((Button)window.FindName("CommandsPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        commands.SelectedIndex = 1;
                        ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 2;
                    }
                    else if (phase == 2 &&
                             ((TextBlock)window.FindName("RunStateText")).Text.StartsWith("已成功", StringComparison.Ordinal) &&
                             output.Text.Contains("group-one", StringComparison.Ordinal))
                    {
                        progress.Stage = "发起限流检查";
                        clock.Advance(TimeSpan.FromSeconds(61));
                        handler.SetScenario("RateLimited");
                        ((Button)window.FindName("AboutPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        manual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 3;
                    }
                    else if (phase == 3 && status.Text.StartsWith("请求受限", StringComparison.Ordinal))
                    {
                        progress.Stage = "限流状态下运行任务";
                        Assert.AreEqual(2, handler.RequestCount);
                        ((Button)window.FindName("CommandsPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        commands.SelectedIndex = 2;
                        ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 4;
                    }
                    else if (phase == 4 &&
                             ((TextBlock)window.FindName("RunStateText")).Text.StartsWith("已成功", StringComparison.Ordinal) &&
                             output.Text.Contains("group-two", StringComparison.Ordinal))
                    {
                        progress.Stage = "发起挂起检查";
                        clock.Advance(TimeSpan.FromSeconds(61));
                        handler.SetScenario("Pending");
                        ((Button)window.FindName("AboutPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        manual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 5;
                    }
                    else if (phase == 5 && handler.RequestCount == 3 && status.Text == "正在手动检查…")
                    {
                        progress.Stage = "挂起检查期间启动服务";
                        ((Button)window.FindName("CommandsPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        commands.SelectedIndex = 0;
                        ((Button)window.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 6;
                    }
                    else if (phase == 6 &&
                             ((Button)window.FindName("StopButton")).IsEnabled &&
                             output.Text.Contains("update-regression-started-1", StringComparison.Ordinal) &&
                             output.Text.Contains("update-regression-stderr", StringComparison.Ordinal))
                    {
                        progress.Stage = "挂起检查期间重启服务";
                        ((Button)window.FindName("RestartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 7;
                    }
                    else if (phase == 7 &&
                             ((Button)window.FindName("StopButton")).IsEnabled &&
                             output.Text.Contains("update-regression-started-2", StringComparison.Ordinal))
                    {
                        progress.Stage = "验证选择复制并停止服务";
                        var marker = output.Text.IndexOf("update-regression-started-2", StringComparison.Ordinal);
                        Assert.IsGreaterThanOrEqualTo(0, marker);
                        output.Select(marker, "update-regression-started-2".Length);
                        var copySelected = (Button)window.FindName("CopySelectedOutputButton");
                        Assert.IsTrue(copySelected.IsEnabled);
                        copySelected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual("已复制所选输出", ((TextBlock)window.FindName("SaveStatusText")).Text);
                        ((Button)window.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        phase = 8;
                    }
                    else if (phase == 8 &&
                             (((TextBlock)window.FindName("RunStateText")).Text.StartsWith("已停止", StringComparison.Ordinal) ||
                              ((TextBlock)window.FindName("RunStateText")).Text.StartsWith("已强制停止", StringComparison.Ordinal)))
                    {
                        progress.Stage = "读取历史 stderr 并运行顺序分组";
                        ((Button)window.FindName("OutputPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var history = (ComboBox)window.FindName("RunHistoryComboBox");
                        if (history.Items.Count < 2) return;
                        history.SelectedIndex = 1;
                        ((ComboBox)window.FindName("HistoryStreamComboBox")).SelectedIndex = 1;
                        StringAssert.Contains(output.Text, "update-regression-stderr");
                        ((Button)window.FindName("GroupsPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        RunGroup(window, 0);
                        phase = 9;
                    }
                    else if (phase == 9 && window.GroupItems[0].StatusText.StartsWith("已完成", StringComparison.Ordinal))
                    {
                        progress.Stage = "运行同时分组";
                        StringAssert.Contains(output.Text, "group-two");
                        ((Button)window.FindName("GroupsPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        RunGroup(window, 1);
                        phase = 10;
                    }
                    else if (phase == 10 && window.GroupItems[1].StatusText.StartsWith("已完成", StringComparison.Ordinal))
                    {
                        progress.Stage = "关闭窗口";
                        Assert.AreEqual(3, handler.RequestCount);
                        ((Button)window.FindName("AboutPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual(Visibility.Visible, ((Grid)window.FindName("AboutPage")).Visibility);
                        Assert.AreEqual(1050d, window.MinWidth);
                        Assert.AreEqual(650d, window.MinHeight);
                        phase = 11;
                        timer.Stop();
                        window.Close();
                        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                    progress.Phase = phase;
                }
                catch (Exception exception)
                {
                    scenarioFailure = exception;
                    timer.Stop();
                    window.Close();
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            };

            progress.Stage = "显示窗口";
            window.Show();
            timer.Start();
            Dispatcher.Run();
            if (scenarioFailure is not null) throw scenarioFailure;
            Assert.AreEqual(11, phase, "桌面流程提前结束。");
            Assert.IsTrue(SpinWait.SpinUntil(() => handler.CancellationObserved, TimeSpan.FromSeconds(2)), "确认退出没有取消挂起的更新请求。");
        }
        finally
        {
            Directory.Delete(sandbox, true);
        }
    }

    private static void RunGroup(MainWindow window, int index)
    {
        var groups = (ListBox)window.FindName("GroupList");
        groups.UpdateLayout();
        var container = (ListBoxItem?)groups.ItemContainerGenerator.ContainerFromIndex(index);
        var runGroup = FindButton(container, "运行分组");
        Assert.IsNotNull(runGroup, $"没有找到第 {index + 1} 个分组的运行按钮。");
        runGroup!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static Button? FindButton(DependencyObject? root, string content)
    {
        if (root is null) return null;
        if (root is Button { Content: string value } button && string.Equals(value, content, StringComparison.Ordinal)) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindButton(VisualTreeHelper.GetChild(root, index), content) is { } found) return found;
        }
        return null;
    }

    private sealed class ScenarioHandler(string scenario) : HttpMessageHandler
    {
        private int _requestCount;
        private int _cancellationObserved;
        private string _scenario = scenario;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;
        public void SetScenario(string value) => Volatile.Write(ref _scenario, value);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var current = Volatile.Read(ref _scenario);
            if (string.Equals(current, "NetworkFailure", StringComparison.Ordinal))
                throw new HttpRequestException("synthetic update failure");
            if (string.Equals(current, "RateLimited", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _cancellationObserved, 1);
                throw;
            }
            throw new InvalidOperationException("无法到达。");
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow = _utcNow.Add(elapsed);
            _timestamp += elapsed.Ticks;
        }
    }

    private sealed class RecordingLauncher : IReleasePageLauncher
    {
        public void Open(string address) => Assert.Fail("未点击发布页面时不应调用浏览器。");
    }

    private sealed class ProgressState
    {
        public string Stage { get; set; } = "未启动";
        public int Phase { get; set; } = -1;
    }
}
