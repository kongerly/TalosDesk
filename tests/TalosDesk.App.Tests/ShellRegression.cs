using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using TalosDesk.App.Editing;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class ShellRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.ShellDesktop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        var cmd = new CommandDefinition { Name = "Cmd 任务", Shell = CommandShell.Cmd, Command = "echo 中文 Cmd& echo first>first.marker& exit /b 0", WorkingDirectory = root };
        var ps = new CommandDefinition { Name = "旧 PowerShell 任务", Command = "if (-not (Test-Path './first.marker')) { exit 8 }; Write-Output 'PowerShell-compatible'; Set-Content './second.marker' 'ok'", WorkingDirectory = root };
        var group = new CommandGroupDefinition { Name = "混合顺序", ExecutionMode = CommandGroupExecutionMode.Sequential, CommandIds = [cmd.Id, ps.Id] };
        var project = new ProjectDefinition { Name = "Shell 回归", Directory = root, Commands = [cmd, ps], Groups = [group] };
        var config = new WorkspaceConfiguration { Projects = [project] };
        store.SaveAsync(config).GetAwaiter().GetResult();
        var window = new MainWindow(store, "隔离 Shell", null, null, null, trayIcon: new TestTrayIcon());
        var loads = 0;
        window.Projects.CollectionChanged += (_, args) => loads += args.NewItems?.Count ?? 0;
        DesktopTestHost.RunWithCleanup(() =>
        {
            window.Show();
            Wait(() => loads >= 2 && ((Button)window.FindName("ManualUpdateCheckButton")).IsEnabled);
            Stage = "编辑、Shell 选择与补全状态";
            var editor = new CommandEditorWindow(root, cmd) { Owner = window };
            try
            {
                Assert.AreEqual(1, ((ComboBox)editor.FindName("ShellBox")).SelectedIndex);
                var code = (PowerShellCommandEditor)editor.FindName("CommandBox");
                Assert.AreEqual(CommandShell.Cmd, code.Shell);
                Assert.IsNull(((TextEditor)code.FindName("Editor")).SyntaxHighlighting);
                ((ComboBox)editor.FindName("ShellBox")).SelectedIndex = 0;
                Assert.IsNotNull(((TextEditor)code.FindName("Editor")).SyntaxHighlighting);
                ((ComboBox)editor.FindName("ShellBox")).SelectedIndex = 1;
                editor.Dispatcher.BeginInvoke(new Action(() => Invoke(editor, "SaveCommand")));
                Assert.IsTrue(editor.ShowDialog());
                Assert.AreEqual(CommandShell.Cmd, editor.Result!.Shell);
                Assert.AreEqual(cmd.Command, editor.Result.Command);
            }
            finally { if (editor.IsVisible) editor.Close(); }

            Stage = "复制、导入预览、冲突替换与保留不运行";
            var clone = (CommandDefinition)typeof(MainWindow).GetMethod("CloneCommand", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [cmd])!;
            Assert.AreEqual(CommandShell.Cmd, clone.Shell);
            var preview = (string)Invoke(window, "BuildImportPreview", config)!;
            StringAssert.Contains(preview, "Windows Cmd");
            StringAssert.Contains(preview, "PowerShell 7");
            var incoming = (ProjectDefinition)typeof(MainWindow).GetMethod("CloneProject", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [project])!;
            incoming.Commands[0].Shell = CommandShell.PowerShell;
            foreach (var choice in new[] { "PrimaryActionButton", "NegativeActionButton", "CancelActionButton" })
            {
                var target = new List<ProjectDefinition> { (ProjectDefinition)typeof(MainWindow).GetMethod("CloneProject", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [project])! };
                var merged = Respond(() => (bool)Invoke(window, "MergeImportedWorkspace", new WorkspaceConfiguration { Projects = [incoming] }, target)!, dialog =>
                    ((Button)dialog.FindName(choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                Assert.AreEqual(choice != "CancelActionButton", merged);
                Assert.AreEqual(choice == "PrimaryActionButton" ? CommandShell.PowerShell : CommandShell.Cmd, target[0].Commands[0].Shell);
            }
            Assert.IsFalse(File.Exists(Path.Combine(root, "first.marker")));
            Assert.HasCount(0, Directory.GetFiles(root, "run.json", SearchOption.AllDirectories));

            Stage = "真实 Cmd 与默认 PowerShell 混合顺序分组";
            var activeProject = window.Projects[0];
            Invoke(window, "StartSequentialGroup", activeProject, activeProject.Groups[0]);
            Wait(() => File.Exists(Path.Combine(root, "second.marker")) &&
                ((Dictionary<Guid, Task>)Field(window, "_sequentialGroupTasks")).Count == 0);
            Wait(() => Directory.GetFiles(root, "run.json", SearchOption.AllDirectories).Length == 2);
            var logs = new RunLogStore(store.FilePath);
            logs.Initialize();
            Assert.AreEqual("Succeeded", logs.GetRuns(project.Id, cmd.Id).Single().State);
            Assert.AreEqual("Succeeded", logs.GetRuns(project.Id, ps.Id).Single().State);
            Assert.IsTrue(logs.ReadTail(logs.GetRuns(project.Id, cmd.Id).Single(), "stdout").Any(line => line == "中文 Cmd"));
            Assert.IsTrue(logs.ReadTail(logs.GetRuns(project.Id, ps.Id).Single(), "stdout").Contains("PowerShell-compatible"));
            window.Close();
            Wait(() => !window.IsVisible);
        }, () => { window.Close(); Wait(() => !window.IsVisible); Directory.Delete(root, true); });
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static T Respond<T>(Func<T> action, Action<Window> respond)
    {
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().LastOrDefault(candidate => candidate.IsVisible && candidate is AppMessageDialogWindow && !handled.Contains(candidate));
            if (dialog is null) return;
            handled.Add(dialog);
            try { respond(dialog); }
            catch (Exception exception) { failure = exception; dialog.Close(); }
        };
        timer.Start();
        try { var result = action(); if (failure is not null) throw failure; return result; }
        finally { timer.Stop(); }
    }

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Shell 桌面检查超时：{Stage}");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
