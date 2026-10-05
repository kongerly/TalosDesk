using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App.Tests;

internal static class WorkspaceSaveRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        RunScenario(afterCommit: true);
        RunScenario(afterCommit: false);
    }

    private static void RunScenario(bool afterCommit)
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.WorkspaceSaveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "workspace.json");
        var externalPath = Path.Combine(root, "external.json");
        new WorkspaceStore(path).SaveAsync(Configuration("原始版本")).GetAwaiter().GetResult();
        new WorkspaceStore(externalPath).SaveAsync(Configuration("外部版本")).GetAwaiter().GetResult();
        var externalBytes = File.ReadAllBytes(externalPath);
        var armed = 0;
        var replacementCount = 0;
        byte[]? committedBytes = null;
        Action replace = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0) return;
            if (afterCommit) committedBytes = File.ReadAllBytes(path);
            File.Move(externalPath, path, overwrite: true);
            Interlocked.Increment(ref replacementCount);
        };
        var store = new WorkspaceStore(path, afterCommit ? null : replace, afterCommit ? replace : null);
        var icon = new TestTrayIcon();
        var window = new MainWindow(store, "隔离保存竞争回归", null, null, null, trayIcon: icon);
        var projectLoads = 0;
        window.Projects.CollectionChanged += (_, args) => projectLoads += args.NewItems?.Count ?? 0;
        DesktopTestHost.RunWithCleanup(() =>
        {
            Stage = "加载与正常保存";
            window.Show();
            Wait(() => projectLoads >= 2 && !Field<bool>(window, "_workspaceChangeInProgress") &&
                Find<Button>(window, "ManualUpdateCheckButton").IsEnabled);
            EditProject(window, "正常保存", expectConflict: false);
            Assert.AreEqual("正常保存", new WorkspaceStore(path).LoadAsync().GetAwaiter().GetResult().Projects.Single().Name);
            var previousRevision = Field<WorkspaceRevision>(window, "_workspaceRevision");

            Stage = afterCommit ? "替换后、保存返回前发生外部修改" : "临时文件写完、替换前发生外部修改";
            Interlocked.Exchange(ref armed, 1);
            EditProject(window, "本机新版本", expectConflict: !afterCommit);
            Assert.AreEqual(1, replacementCount);
            CollectionAssert.AreEqual(externalBytes, File.ReadAllBytes(path));

            if (afterCommit)
            {
                Assert.IsNotNull(committedBytes);
                var savedRevision = Field<WorkspaceRevision>(window, "_workspaceRevision");
                Assert.AreEqual(new WorkspaceRevision(true, Convert.ToHexString(SHA256.HashData(committedBytes))), savedRevision);
                Assert.AreNotEqual(store.GetRevisionAsync().GetAwaiter().GetResult(), savedRevision);
                Stage = "下一次保存拒绝覆盖外部版本并回滚编辑";
                EditProject(window, "不应写入", expectConflict: true);
                Assert.AreEqual("本机新版本", window.Projects.Single().Name);
                Assert.AreEqual(savedRevision, Field<WorkspaceRevision>(window, "_workspaceRevision"));
            }
            else
            {
                Assert.AreEqual("正常保存", window.Projects.Single().Name);
                Assert.AreEqual(previousRevision, Field<WorkspaceRevision>(window, "_workspaceRevision"));
            }

            Assert.IsFalse(Field<bool>(window, "_canSave"));
            Assert.IsFalse(Find<Button>(window, "EditProjectButton").IsEnabled);
            Assert.IsFalse(Find<Button>(window, "AddProjectButton").IsEnabled);
            Assert.IsFalse(Save(window).GetAwaiter().GetResult(), "冲突后不能再保存或重复弹窗。");
            CollectionAssert.AreEqual(externalBytes, File.ReadAllBytes(path));
            Assert.HasCount(0, Directory.GetFiles(root, "*.tmp"));

            Stage = "重新加载外部版本后恢复保存";
            var reload = window.ReloadWorkspaceForSecondaryLaunchAsync();
            Wait(() => reload.IsCompleted);
            reload.GetAwaiter().GetResult();
            Assert.AreEqual("外部版本", window.Projects.Single().Name);
            Assert.IsTrue(Find<Button>(window, "EditProjectButton").IsEnabled);
            EditProject(window, "重新加载后保存", expectConflict: false);
            Assert.AreEqual("重新加载后保存", store.LoadAsync().GetAwaiter().GetResult().Projects.Single().Name);
        }, () =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<AppMessageDialogWindow>().ToArray()) dialog.Close();
            foreach (var editor in Application.Current.Windows.OfType<ProjectEditorWindow>().ToArray()) editor.Close();
            Wait(() => !Field<bool>(window, "_workspaceChangeInProgress"));
            window.Close();
            Wait(() => icon.DisposeCount == 1);
            Directory.Delete(root, true);
        });

        WorkspaceConfiguration Configuration(string name) => new() { Projects = [new() { Name = name, Directory = root }] };
    }

    private static void EditProject(MainWindow window, string name, bool expectConflict)
    {
        var editorHandled = false;
        var conflicts = 0;
        Exception? failure = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var editor = Application.Current.Windows.OfType<ProjectEditorWindow>().FirstOrDefault(item => item.IsVisible);
            var dialog = Application.Current.Windows.OfType<AppMessageDialogWindow>().FirstOrDefault(item => item.IsVisible);
            try
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, $"等待编辑或提示超时：{Stage}。");
                if (editor is not null && !editorHandled)
                {
                    editorHandled = true;
                    Find<TextBox>(editor, "NameBox").Text = name;
                    Find<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                if (dialog is not null)
                {
                    Assert.IsTrue(expectConflict, $"意外提示：{dialog.Title}。");
                    Assert.AreEqual("TalosDesk · 检测到外部配置变化", dialog.Title);
                    conflicts++;
                    dialog.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                dialog?.Close();
                editor?.Close();
            }
        };
        timer.Start();
        try
        {
            Find<Button>(window, "EditProjectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => (failure is not null || editorHandled) && !Field<bool>(window, "_workspaceChangeInProgress"));
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.AreEqual(expectConflict ? 1 : 0, conflicts);
        }
        finally { timer.Stop(); }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static Task<bool> Save(MainWindow window) =>
        (Task<bool>)typeof(MainWindow).GetMethod("SaveWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

    private static T Find<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"保存竞争检查超时：{Stage}。");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
