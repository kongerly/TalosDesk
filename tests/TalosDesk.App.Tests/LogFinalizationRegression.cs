using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class LogFinalizationRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run(int exitCode)
    {
        Stage = "建立独立工作区";
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.LogFinalizationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var release = Path.Combine(root, "release-command");
        var command = new CommandDefinition
        {
            Name = "合成日志收尾", WorkingDirectory = root,
            Command = "Write-Output 'finalization-ready'; while (-not (Test-Path -LiteralPath './release-command')) { Start-Sleep -Milliseconds 20 }; exit " +
                exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var project = new ProjectDefinition { Name = "隔离收尾回归", Directory = root, Commands = [command] };
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        store.SaveAsync(new() { Projects = [project] }).GetAwaiter().GetResult();
        var icon = new TestTrayIcon();
        var window = new MainWindow(store, "隔离收尾回归", null, null, null,
            trayIcon: icon, exitInteraction: new ExitInteraction());
        FileStream? lockedMetadata = null;
        var projectLoads = 0;
        window.Projects.CollectionChanged += (_, args) => projectLoads += args.NewItems?.Count ?? 0;
        DesktopTestHost.RunWithCleanup(() =>
        {
            window.Show();
            Wait(() => projectLoads >= 2 && Find<Button>("ManualUpdateCheckButton").IsEnabled);
            Find<Button>("OutputPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<ListBox>("CommandList").SelectedIndex = 0;
            var writers = Field<Dictionary<Guid, RunLogWriter>>(window, "_runLogWriters");
            var sessions = Field<Dictionary<Guid, CommandRunSession>>(window, "_sessions");
            var results = Field<Dictionary<Guid, CommandRunResult>>(window, "_lastResults");
            var logs = Field<RunLogStore>(window, "_runLogStore");

            Stage = "锁住完成记录后结束真实命令";
            Start();
            var first = writers[command.Id];
            var metadata = Path.Combine(logs.RootPath, first.Info.ProjectId.ToString("N"), first.Info.CommandId.ToString("N"),
                first.Info.RunId.ToString("N"), "run.json");
            var original = File.ReadAllBytes(metadata);
            lockedMetadata = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read);
            File.WriteAllText(release, "synthetic");
            Wait(() => !sessions.ContainsKey(command.Id) && !writers.ContainsKey(command.Id));
            Assert.IsTrue(first.Info.WriteFailed);
            Assert.AreEqual("日志完成写入失败。", Find<TextBlock>("LogStatusText").Text);
            AssertResult();
            CollectionAssert.AreEqual(original, File.ReadAllBytes(metadata));
            Assert.IsFalse(File.Exists(metadata + ".tmp"));
            StringAssert.Contains(Find<TextBox>("OutputTextBox").Text, "finalization-ready");
            Assert.IsTrue(Find<Button>("RunButton").IsEnabled);
            Assert.IsFalse(Find<Button>("StopButton").IsEnabled);

            Stage = "解除文件占用后新运行清除旧警告";
            lockedMetadata.Dispose();
            lockedMetadata = null;
            File.Delete(release);
            Start();
            var second = writers[command.Id];
            Assert.AreNotEqual(first.Info.RunId, second.Info.RunId);
            Assert.AreEqual(string.Empty, Find<TextBlock>("LogStatusText").Text);
            File.WriteAllText(release, "synthetic");
            Wait(() => !sessions.ContainsKey(command.Id) && !writers.ContainsKey(command.Id));
            Assert.IsFalse(second.Info.WriteFailed);
            Assert.AreEqual(string.Empty, Find<TextBlock>("LogStatusText").Text);
            AssertResult();

            void Start()
            {
                Find<Button>("RunButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wait(() => writers.ContainsKey(command.Id) && Find<TextBox>("OutputTextBox").Text.Contains("finalization-ready", StringComparison.Ordinal));
            }

            void AssertResult()
            {
                var expected = exitCode == 0 ? CommandRunState.Succeeded : CommandRunState.Failed;
                Assert.AreEqual(expected, results[command.Id].State);
                Assert.AreEqual(exitCode, results[command.Id].ExitCode);
                StringAssert.StartsWith(Find<TextBlock>("RunStateText").Text, exitCode == 0 ? "已成功" : "失败");
            }
        }, () =>
        {
            lockedMetadata?.Dispose();
            File.WriteAllText(release, "synthetic");
            window.Close();
            Wait(() => icon.DisposeCount == 1);
            Directory.Delete(root, true);
        });

        T Find<T>(string name) => (T)window.FindName(name);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"日志收尾回归超时：{Stage}");
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
