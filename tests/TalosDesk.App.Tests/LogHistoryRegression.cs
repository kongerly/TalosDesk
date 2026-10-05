using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class LogHistoryRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.LogHistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        var firstCommand = new CommandDefinition { Name = "历史与实时输出", Command = "Write-Output 'live-log-marker'", WorkingDirectory = root };
        var secondCommand = new CommandDefinition { Name = "另一个命令", Command = "Write-Output 'other-command'", WorkingDirectory = root };
        var project = new ProjectDefinition { Name = "历史回归项目", Directory = root, Commands = [firstCommand, secondCommand] };
        var otherRoot = Path.Combine(root, "other-project");
        Directory.CreateDirectory(otherRoot);
        var otherProject = new ProjectDefinition { Name = "另一个项目", Directory = otherRoot, Commands = [new() { Name = "其他项目任务", Command = "Write-Output 'other-project'", WorkingDirectory = otherRoot }] };
        store.SaveAsync(new() { Projects = [project, otherProject] }).GetAwaiter().GetResult();
        var requests = new ConcurrentQueue<ReadRequest>();
        var uiThreadId = Environment.CurrentManagedThreadId;
        var window = new MainWindow(store, "隔离历史日志测试", null, null, null, trayIcon: new TestTrayIcon(),
            readLogTail: (info, stream, token) =>
            {
                Assert.AreNotEqual(uiThreadId, Environment.CurrentManagedThreadId, "磁盘历史读取必须在后台执行。");
                var request = new ReadRequest(info.RunId, stream, token);
                requests.Enqueue(request);
                // 合成读取不需要取消传播：故意传入 CancellationToken.None 表示该测试不参与取消。
                try { return request.Result.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult(); }
                catch (TimeoutException) { throw new IOException("合成读取未及时释放。"); }
            });
        var projectLoads = 0;
        window.Projects.CollectionChanged += (_, args) => projectLoads += args.NewItems?.Count ?? 0;
        var pending = new List<ReadRequest>();
        DesktopTestHost.RunWithCleanup(() =>
        {
            window.Show();
            Wait(() => projectLoads >= 4 && Find<Button>("ManualUpdateCheckButton").IsEnabled);
            Find<Button>("OutputPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<ListBox>("CommandList").SelectedIndex = 0;
            var output = Find<TextBox>("OutputTextBox");
            var first = new RunLogInfo { ProjectId = project.Id, CommandId = firstCommand.Id, RunId = Guid.NewGuid(), StartedAt = DateTimeOffset.Now, State = "Succeeded" };
            var second = new RunLogInfo { ProjectId = project.Id, CommandId = firstCommand.Id, RunId = Guid.NewGuid(), StartedAt = DateTimeOffset.Now.AddMinutes(-1), State = "Failed", WriteFailed = true };

            Stage = "慢读取期间界面响应，切换通道拒绝旧结果";
            var stdout = Select(first, "stdout");
            Assert.AreEqual(string.Empty, output.Text);
            Assert.AreEqual("正在读取历史日志…", Find<TextBlock>("EmptyOutputHint").Text);
            Assert.IsFalse(Find<Button>("ClearOutputButton").IsEnabled);
            var responded = false;
            window.Dispatcher.BeginInvoke(new Action(() => responded = true));
            Wait(() => responded);
            Find<ComboBox>("HistoryStreamComboBox").SelectedIndex = 1;
            var stderr = Take(first, "stderr");
            Assert.IsTrue(stdout.Token.IsCancellationRequested);
            Finish(stderr, ["new-stderr"]);
            Finish(stdout, ["stale-stdout"]);
            Assert.AreEqual("new-stderr", output.Text);

            Stage = "切换批次拒绝迟到错误并显示空通道与完整性警告";
            var old = Select(first, "stdout");
            var current = Select(second, "stderr");
            Finish(current, []);
            old.Result.SetException(new IOException("stale-read-failure"));
            Wait(() => IsDisposed(old.Source));
            Assert.AreEqual("该批次的此输出通道没有内容。", Find<TextBlock>("EmptyOutputHint").Text);
            Assert.AreEqual("该批次日志写入失败，内容不完整。", Find<TextBlock>("LogStatusText").Text);

            Stage = "当前读取错误只影响当前通道";
            var failed = Select(first, "stdout");
            failed.Result.SetException(new UnauthorizedAccessException("synthetic-read-error"));
            Wait(() => IsDisposed(failed.Source));
            StringAssert.Contains(Find<TextBlock>("LogStatusText").Text, "synthetic-read-error");
            Assert.AreEqual("该批次的此输出通道读取失败。", Find<TextBlock>("EmptyOutputHint").Text);

            Stage = "历史读取期间运行合成任务，返回当前显示后保持实时输出";
            var beforeRun = Select(first, "stdout");
            Find<Button>("RunButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => output.Text.Contains("live-log-marker", StringComparison.Ordinal) &&
                Find<TextBlock>("RunStateText").Text.StartsWith("已成功", StringComparison.Ordinal));
            Assert.IsTrue(beforeRun.Token.IsCancellationRequested);
            Finish(beforeRun, ["stale-before-run"]);
            StringAssert.Contains(output.Text, "live-log-marker");
            Assert.IsFalse(output.Text.Contains("stale-before-run", StringComparison.Ordinal));

            Stage = "切换命令与项目取消读取";
            var beforeCommand = Select(first, "stdout");
            Find<ListBox>("CommandList").SelectedIndex = 1;
            Assert.IsTrue(beforeCommand.Token.IsCancellationRequested);
            Finish(beforeCommand, ["stale-command"]);
            Assert.AreEqual(string.Empty, output.Text);
            Find<ListBox>("CommandList").SelectedIndex = 0;
            var beforeProject = Select(first, "stdout");
            Find<ListBox>("ProjectList").SelectedIndex = 1;
            Assert.IsTrue(beforeProject.Token.IsCancellationRequested);
            Finish(beforeProject, ["stale-project"]);
            Assert.AreEqual(string.Empty, output.Text);

            Stage = "关闭窗口不等待历史读取，迟到错误不能修改已关闭窗口";
            Find<ListBox>("ProjectList").SelectedIndex = 0;
            Find<ListBox>("CommandList").SelectedIndex = 0;
            var beforeClose = Select(first, "stdout");
            window.Close();
            Wait(() => !window.IsVisible);
            Assert.IsTrue(beforeClose.Token.IsCancellationRequested);
            var status = Find<TextBlock>("LogStatusText").Text;
            beforeClose.Result.SetException(new IOException("closed-read-failure"));
            Wait(() => IsDisposed(beforeClose.Source));
            Assert.AreEqual(status, Find<TextBlock>("LogStatusText").Text);
        }, () =>
        {
            foreach (var request in pending) request.Result.TrySetResult([]);
            foreach (var request in requests) request.Result.TrySetResult([]);
            window.Close();
            Wait(() => !window.IsVisible && pending.All(request => IsDisposed(request.Source)));
            Directory.Delete(root, true);
        });

        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);

        ReadRequest Select(RunLogInfo info, string stream)
        {
            var history = Find<ComboBox>("RunHistoryComboBox");
            history.SelectedIndex = 0;
            Find<ComboBox>("HistoryStreamComboBox").SelectedIndex = stream == "stderr" ? 1 : 0;
            var item = new RunHistoryItem("合成历史", info);
            ((ObservableCollection<RunHistoryItem>)history.ItemsSource).Add(item);
            history.SelectedItem = item;
            return Take(info, stream);
        }

        ReadRequest Take(RunLogInfo info, string stream)
        {
            var source = (CancellationTokenSource)typeof(MainWindow).GetField("_historyReadCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Wait(() => !requests.IsEmpty);
            Assert.IsTrue(requests.TryDequeue(out var request));
            Assert.AreEqual(info.RunId, request.RunId);
            Assert.AreEqual(stream, request.Stream);
            request.Source = source;
            pending.Add(request);
            return request;
        }

        void Finish(ReadRequest request, IReadOnlyList<string> lines)
        {
            request.Result.SetResult(lines);
            Wait(() => IsDisposed(request.Source));
        }
    }

    private static bool IsDisposed(CancellationTokenSource source)
    {
        try { _ = source.Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"历史日志检查超时：{Stage}。");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class ReadRequest(Guid runId, string stream, CancellationToken token)
    {
        internal Guid RunId { get; } = runId;
        internal string Stream { get; } = stream;
        internal CancellationToken Token { get; } = token;
        internal CancellationTokenSource Source { get; set; } = null!;
        internal TaskCompletionSource<IReadOnlyList<string>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
