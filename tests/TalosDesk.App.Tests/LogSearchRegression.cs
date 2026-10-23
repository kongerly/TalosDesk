using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class LogSearchRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.LogSearch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        var command = new CommandDefinition { Name = "搜索合成日志", Command = "exit 0", WorkingDirectory = root };
        var project = new ProjectDefinition { Name = "搜索测试", Directory = root, Commands = [command, new() { Name = "空命令", Command = "exit 0", WorkingDirectory = root }] };
        store.SaveAsync(new() { Projects = [project] }).GetAwaiter().GetResult();
        var logs = new RunLogStore(store.FilePath);
        logs.Initialize();
        var first = logs.Begin(project.Id, command.Id, DateTimeOffset.Now.AddMinutes(-2));
        logs.Append(first, new(DateTimeOffset.Now, "stdout", "中文 历史甲 中文"));
        logs.Append(first, new(DateTimeOffset.Now, "stderr", "中文 诊断"));
        logs.Complete(first, new(CommandRunState.Succeeded, 0, false));
        var second = logs.Begin(project.Id, command.Id, DateTimeOffset.Now.AddMinutes(-1));
        logs.Append(second, new(DateTimeOffset.Now, "stdout", "历史乙"));
        logs.Complete(second, new(CommandRunState.Failed, 7, false));
        var window = new MainWindow(store, "日志搜索隔离", null, null, null, trayIcon: new TestTrayIcon());
        var projectLoads = 0;
        window.Projects.CollectionChanged += (_, args) => projectLoads += args.NewItems?.Count ?? 0;
        DesktopTestHost.RunWithCleanup(() =>
        {
            window.Show();
            Wait(() => projectLoads >= 2 && Find<Button>("ManualUpdateCheckButton").IsEnabled);
            Find<ListBox>("CommandList").SelectedIndex = 0;
            Stage = "Ctrl+F 路由、历史中文、导航循环与高亮";
            Assert.IsTrue(window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.F && binding.Modifiers == ModifierKeys.Control));
            ApplicationCommands.Find.Execute(null, window);
            var output = Find<TextBox>("OutputTextBox");
            var search = Find<TextBox>("LogSearchBox");
            search.Text = "中文";
            var history = Find<ComboBox>("RunHistoryComboBox");
            history.SelectedItem = history.Items.Cast<RunHistoryItem>().Single(item => item.Info?.RunId == first.Info.RunId);
            Wait(() => Count() == "0 / 2");
            StringAssert.Contains(Find<TextBlock>("LogSearchScopeText").Text, "所选历史批次");
            Assert.IsTrue(AdornerLayer.GetAdornerLayer(output).GetAdorners(output)!.OfType<LogSearchAdorner>().Any());
            Click("NextLogMatchButton");
            Assert.AreEqual("1 / 2", Count());
            Assert.AreEqual("中文", output.SelectedText);
            Click("PreviousLogMatchButton");
            Assert.AreEqual("2 / 2", Count());
            Click("NextLogMatchButton");
            Assert.AreEqual("1 / 2", Count());

            Stage = "通道、批次和空结果不沿用匹配";
            Find<ComboBox>("HistoryStreamComboBox").SelectedIndex = 1;
            Assert.IsFalse(Find<Button>("NextLogMatchButton").IsEnabled);
            Wait(() => Count() == "0 / 1");
            StringAssert.Contains(Find<TextBlock>("LogSearchScopeText").Text, "诊断输出");
            history.SelectedItem = history.Items.Cast<RunHistoryItem>().Single(item => item.Info?.RunId == second.Info.RunId);
            Wait(() => output.Text == string.Empty && Count() == "0 / 0");
            Find<ComboBox>("HistoryStreamComboBox").SelectedIndex = 0;
            Wait(() => output.Text == "历史乙");
            search.Text = "不存在";
            Wait(() => Count() == "0 / 0");
            Assert.IsFalse(Find<Button>("NextLogMatchButton").IsEnabled);

            Stage = "实时追加、通道限制与清空显示";
            history.SelectedIndex = 0;
            var memory = (ObservableCollection<CommandOutput>)Invoke("GetLogs", command.Id)!;
            memory.Add(new(DateTimeOffset.Now, "stdout", "中文 [已隐藏]"));
            memory.Add(new(DateTimeOffset.Now, "stderr", "中文 诊断"));
            Invoke("RenderSelectedOutput", command, true, null);
            search.Text = "中文";
            Wait(() => Count() == "0 / 1");
            memory.Add(new(DateTimeOffset.Now, "stdout", "中文 追加"));
            Invoke("AppendSelectedOutputBatch", new CommandOutput[] { memory.Last() }, false);
            Wait(() => Count() == "0 / 2");
            Find<ComboBox>("HistoryStreamComboBox").SelectedIndex = 1;
            Wait(() => Count() == "0 / 1");
            Click("ClearOutputButton");
            Assert.IsFalse(Find<Button>("NextLogMatchButton").IsEnabled);
            Wait(() => Count() == "0 / 0");
            Assert.AreEqual("中文", search.Text);
            Assert.HasCount(2, logs.GetRuns(project.Id, command.Id));

            Stage = "10,000行性能、请求失效和切换命令";
            Find<ComboBox>("HistoryStreamComboBox").SelectedIndex = 0;
            var clock = Stopwatch.StartNew();
            for (var index = 0; index < 10_000; index++) memory.Add(new(DateTimeOffset.Now, "stdout", $"中文 合成行 {index}"));
            Invoke("RenderSelectedOutput", command, true, null);
            var responsive = false;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => responsive = true));
            Wait(() => responsive && Count() == "0 / 10000");
            Console.WriteLine($"搜索10,000行及WPF显示耗时：{clock.Elapsed.TotalMilliseconds:F0} ms。");
            Click("NextLogMatchButton");
            Assert.AreEqual("中文", output.SelectedText);
            search.Text = "合成行";
            Find<ListBox>("CommandList").SelectedIndex = 1;
            Wait(() => output.Text == string.Empty && Count() == "0 / 0");
            Assert.IsFalse(Find<Button>("NextLogMatchButton").IsEnabled);
            window.Close();
            Wait(() => !window.IsVisible);
        }, () => { window.Close(); Wait(() => !window.IsVisible); Directory.Delete(root, true); });

        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        string Count() => Find<TextBlock>("LogMatchCountText").Text;
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    }

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"日志搜索超时：{Stage}");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
