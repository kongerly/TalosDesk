using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App.Tests;

internal static class WindowLayoutRegression
{
    internal static string Stage { get; private set; } = "开始";
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.LayoutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        var command = new CommandDefinition
        {
            Name = "隔离布局测试：用于检查长名称与操作按钮是否重叠的命令",
            Command = "Write-Output 'layout-test'", WorkingDirectory = root, Purpose = "合成布局检查"
        };
        var project = new ProjectDefinition
        {
            Name = "隔离布局测试项目", Directory = root, Commands = [command],
            Groups = [new CommandGroupDefinition { Name = "长名称分组：验证窗口缩小时仍能完整访问操作按钮", CommandIds = [command.Id] }]
        };
        store.SaveAsync(new WorkspaceConfiguration { Projects = [project] }).GetAwaiter().GetResult();
        MainWindow? window = null;
        try
        {
            window = Open(store);
            foreach (var size in new[] { new Size(800, 520), new Size(1050, 650), new Size(1199, 700), new Size(1200, 700), new Size(1400, 860) })
            {
                Stage = $"调整大小 {size}";
                window.Width = size.Width;
                window.Height = size.Height;
                Pump();
                AssertButtonsReachable(window, (FrameworkElement)window.FindName("SidebarScroll"));
                foreach (var page in new[] { "Overview", "Commands", "Groups", "Output", "About" })
                {
                    Stage = $"检查 {page} {size}";
                    ((Button)window.FindName(page + "PageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();
                    AssertButtonsReachable(window, (FrameworkElement)window.FindName(page + "Page"));
                    if (page == "Output")
                    {
                        var combo = (ComboBox)window.FindName("OutputCommandComboBox");
                        var list = (ListBox)window.FindName("OutputCommandList");
                        Assert.AreEqual(size.Width < 1200, combo.IsVisible);
                        if (combo.IsVisible) combo.SelectedIndex = 0;
                        else list.SelectedIndex = 0;
                        Assert.AreSame(list.SelectedItem, combo.SelectedItem);
                        var expander = (Expander)window.FindName("LogSettingsExpander");
                        expander.IsExpanded = true;
                        Pump();
                        AssertButtonsReachable(window, (FrameworkElement)window.FindName("OutputPage"));
                        expander.IsExpanded = false;
                    }
                    ((ScrollViewer)window.FindName("MainContentScroll")).ScrollToTop();
                    ((ScrollViewer)window.FindName("OutputDetailScroll")).ScrollToTop();
                    Pump();
                    Capture(window, $"main-{page}-{size.Width}x{size.Height}");
                }
            }

            Window[] dialogs =
            [
                new ProjectEditorWindow(project), new CommandEditorWindow(root, command),
                new CommandGroupEditorWindow(project.Commands, project.Groups[0]),
                new EnvironmentVariableEditorWindow(null, []), new WorkspaceImportPreviewWindow("隔离导入预览\n合成项目与命令")
            ];
            foreach (var dialog in dialogs)
            {
                try
                {
                    dialog.Owner = window;
                    dialog.Show();
                    dialog.Width = dialog.MinWidth;
                    dialog.Height = dialog.MinHeight;
                    Pump();
                    AssertButtonsReachable(dialog, (FrameworkElement)dialog.Content);
                    Capture(dialog, dialog.GetType().Name);
                }
                finally { dialog.Close(); }
            }

            var preferences = new WindowSettingsStore(store.FilePath);
            window.Width = 1000;
            window.Height = 600;
            window.Left = SystemParameters.WorkArea.Left + 35;
            window.Top = SystemParameters.WorkArea.Top + 40;
            Pump();
            var expected = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            window.Close();
            Assert.IsTrue(File.Exists(preferences.FilePath), "关闭窗口后应自动记录布局。");
            window = Open(store);
            AssertBounds(expected, window);

            var saved = File.ReadAllText(preferences.FilePath);
            CancelEventHandler cancel = (_, args) => args.Cancel = true;
            window.Closing += cancel;
            window.Width = 1100;
            window.Close();
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(saved, File.ReadAllText(preferences.FilePath), "取消退出不应覆盖上次记录。");
            window.Closing -= cancel;
            window.Width = expected.Width;
            Pump();
            window.WindowState = WindowState.Maximized;
            Pump();
            window.Close();
            Assert.IsTrue(preferences.Load().IsMaximized);
            window = Open(store);
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            window.WindowState = WindowState.Minimized;
            Pump();
            window.Close();
            Assert.IsTrue(preferences.Load().IsMaximized, "最小化不能覆盖此前的最大化状态。");
            window = Open(store);
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            window.WindowState = WindowState.Normal;
            Pump();
            AssertBounds(expected, window);
            window.WindowState = WindowState.Minimized;
            Pump();
            window.Close();
            window = Open(store);
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            AssertBounds(expected, window);
        }
        finally
        {
            window?.Close();
            Pump();
            Directory.Delete(root, true);
        }
    }

    private static MainWindow Open(WorkspaceStore store)
    {
        Stage = "创建窗口";
        var window = new MainWindow(store, "隔离布局测试");
        Stage = "显示窗口";
        window.Show();
        Stage = "等待初始化";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!((Button)window.FindName("ManualUpdateCheckButton")).IsEnabled)
        {
            Pump();
            if (DateTime.UtcNow > deadline) Assert.Fail("隔离窗口未能完成初始化。");
        }
        Pump();
        Stage = "窗口就绪";
        return window;
    }

    private static void AssertBounds(Rect expected, Window window)
    {
        Assert.AreEqual(expected.Width, window.ActualWidth, 2);
        Assert.AreEqual(expected.Height, window.ActualHeight, 2);
        Assert.AreEqual(expected.Left, window.Left, 2);
        Assert.AreEqual(expected.Top, window.Top, 2);
    }

    private static void AssertButtonsReachable(Window window, FrameworkElement root)
    {
        foreach (var button in Descendants(root).OfType<Button>().Where(b => b.IsVisible && b.Content is string).ToArray())
        {
            button.BringIntoView();
            Pump();
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            var visible = new Rect(new Point(), window.RenderSize);
            for (DependencyObject? ancestor = VisualTreeHelper.GetParent(button); ancestor is not null && ancestor != window;
                 ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter))
                    visible.Intersect(element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize)));
            }
            Assert.IsTrue(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= visible.Left - 1 &&
                bounds.Right <= visible.Right + 1 && bounds.Top >= visible.Top - 1 && bounds.Bottom <= visible.Bottom + 1,
                $"{window.GetType().Name} {window.ActualWidth}x{window.ActualHeight} 按钮 {button.Content} 被裁切：{bounds}，可见范围 {visible}。");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string name)
    {
        var path = Environment.GetEnvironmentVariable("TALOSDESK_LAYOUT_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(path, name + ".png"));
        encoder.Save(file);
    }
}
