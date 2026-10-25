using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App.Tests;

internal static class MessageDialogRegression
{
    internal static string Stage { get; private set; } = "开始";

    internal static void Run()
    {
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.DialogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        var command = new CommandDefinition { Name = "合成任务", Command = "Write-Output 'dialog-test'", WorkingDirectory = root };
        var project = new ProjectDefinition
        {
            Name = "隔离提示项目", Directory = root, Commands = [command],
            Groups = [new() { Name = "合成分组", CommandIds = [command.Id] }]
        };
        store.SaveAsync(new() { Projects = [project] }).GetAwaiter().GetResult();
        var window = new MainWindow(store, "隔离提示验收", null, null, null, trayIcon: new TestTrayIcon());
        DesktopTestHost.RunWithCleanup(() =>
        {
            CheckInvalidWorkspaceIds(root);
            window.Show();
            Wait(() => Find<Button>(window, "ManualUpdateCheckButton").IsEnabled);
            CheckResultsAndKeyboard(window);
            CheckLayout(window);
            CheckEditorValidation(window);
            CheckImportChoices(window, root);
            CheckDamagedLogHistory(window);
            CheckDeletion(window, store);
            CheckExitInteraction(window);
            CheckRunningExit(root);
            Assert.IsEmpty(Sessions(window), "提示与导入合并不能启动命令。");
        }, () =>
        {
            Wait(() => !(bool)typeof(MainWindow).GetField("_workspaceChangeInProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            window.Close();
            Pump();
            Directory.Delete(root, true);
            System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        });
    }

    internal static void LogSettingsFailureMessagesFollowPersistedState(bool cleanupFailure, bool initializationFailure)
    {
        Stage = initializationFailure ? "设置保存后初始化失败" : cleanupFailure ? "设置保存后清理受阻" : "设置文件替换受阻";
        var root = Path.Combine(Path.GetTempPath(), "TalosDesk.SettingsDialogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new WorkspaceStore(Path.Combine(root, "workspace.json"));
        var command = new CommandDefinition { Name = "合成日志设置任务", WorkingDirectory = root,
            Command = "Set-Content -LiteralPath './unexpected-settings-run.txt' -Value 'synthetic'" };
        store.SaveAsync(new() { Projects = [new() { Name = "隔离日志设置", Directory = root, Commands = [command] }] }).GetAwaiter().GetResult();
        if (initializationFailure) File.WriteAllText(store.FilePath + ".log-location.json", "{");
        var window = new MainWindow(store, "隔离日志设置验收", null, null, null, trayIcon: new TestTrayIcon());
        DesktopTestHost.RunWithCleanup(() =>
        {
            window.Show();
            Wait(() => Find<Button>(window, "ManualUpdateCheckButton").IsEnabled);
            Click(window, "OutputPageButton");
            Find<Expander>(window, "LogSettingsExpander").IsExpanded = true;
            var logs = (RunLogStore)typeof(MainWindow).GetField("_runLogStore", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var previous = new RunLogSettings(2 * 1024 * 1024, 30);
            logs.UpdateSettings(previous);
            var settingsPath = Path.Combine(logs.RootPath, "settings.json");
            var original = File.ReadAllBytes(settingsPath);
            var lockedPath = initializationFailure ? store.FilePath + ".log-location.json" : settingsPath;
            if (cleanupFailure)
            {
                var history = logs.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now.AddDays(-2));
                logs.Append(history, new CommandOutput(DateTimeOffset.Now, "stdout", "synthetic expired output"));
                logs.Complete(history, new CommandRunResult(CommandRunState.Succeeded, 0, false));
                lockedPath = Path.Combine(logs.RootPath, history.Info.ProjectId.ToString("N"), history.Info.CommandId.ToString("N"), history.Info.RunId.ToString("N"), "run.json");
            }
            Find<TextBox>(window, "LogLimitMbTextBox").Text = "1";
            Find<TextBox>(window, "LogRetentionDaysTextBox").Text = "1";
            var requested = new RunLogSettings(1024 * 1024, 1);
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Respond(() => { Click(window, "SaveLogSettingsButton"); return true; }, dialog =>
                {
                    Assert.AreEqual(cleanupFailure || initializationFailure ? "日志设置已保存" : "日志设置未保存", Find<TextBlock>(dialog, "HeadingText").Text);
                    StringAssert.Contains(Find<TextBox>(dialog, "MessageBox").Text,
                        cleanupFailure ? "历史日志清理未完成" : initializationFailure ? "日志初始化未完成" : "无法保存日志设置");
                    Click(dialog, "PrimaryActionButton");
                });
                Assert.AreEqual(cleanupFailure || initializationFailure ? requested : previous, logs.Settings);
                if (cleanupFailure || initializationFailure)
                {
                    Assert.AreEqual(cleanupFailure ? "日志设置已保存，但历史日志清理未完成。" : "日志设置已保存，但日志初始化未完成。",
                        Find<TextBlock>(window, "LogStatusText").Text);
                    Assert.AreEqual(requested, System.Text.Json.JsonSerializer.Deserialize<RunLogSettings>(File.ReadAllText(settingsPath)));
                }
                else CollectionAssert.AreEqual(original, File.ReadAllBytes(settingsPath));
                Assert.IsFalse(File.Exists(settingsPath + ".tmp"));
                Assert.AreEqual(!initializationFailure, (bool)typeof(MainWindow).GetField("_logStoreAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            }
            Stage = "解除占用后再次保存与清理";
            if (initializationFailure) File.Delete(store.FilePath + ".log-location.json");
            Respond(() => { Click(window, "SaveLogSettingsButton"); return true; });
            Assert.AreEqual("日志设置已保存。", Find<TextBlock>(window, "LogStatusText").Text);
            Assert.IsTrue((bool)typeof(MainWindow).GetField("_logStoreAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            var reopened = new RunLogStore(store.FilePath);
            reopened.Initialize();
            Assert.AreEqual(requested, reopened.Settings);
            Assert.IsEmpty(Sessions(window), "日志设置操作不能启动命令。");
            Assert.IsFalse(File.Exists(Path.Combine(root, "unexpected-settings-run.txt")));
        }, () =>
        {
            window.Close();
            Pump();
            Directory.Delete(root, true);
        });
    }

    private static void CheckResultsAndKeyboard(Window owner)
    {
        Stage = "模态所有者、按钮结果与键盘";
        foreach (var image in new[] { MessageBoxImage.Information, MessageBoxImage.Question, MessageBoxImage.Warning, MessageBoxImage.Error })
        {
            var result = Respond(() => AppMessageDialog.Show(owner, "合成中文消息。", "类别检查", MessageBoxButton.OK, image), dialog =>
            {
                Assert.AreSame(owner, dialog.Owner);
                Assert.AreEqual(owner.Left + (owner.ActualWidth - dialog.ActualWidth) / 2, dialog.Left, 2);
                Assert.AreEqual(owner.Top + (owner.ActualHeight - dialog.ActualHeight) / 2, dialog.Top, 2);
                Assert.IsFalse(IsWindowEnabled(new WindowInteropHelper(owner).Handle), "所有者在模态提示期间应不可操作。");
                Assert.IsTrue(Find<Button>(dialog, "PrimaryActionButton").IsKeyboardFocused);
                Assert.AreEqual("确定", Find<Button>(dialog, "PrimaryActionButton").Content);
                Capture(dialog, "message-" + image);
                Press(dialog, Key.Enter);
            });
            Assert.AreEqual(MessageBoxResult.OK, result);
            Assert.IsTrue(IsWindowEnabled(new WindowInteropHelper(owner).Handle));
        }
        foreach (var dismiss in new Action<AppMessageDialogWindow>[] { dialog => Press(dialog, Key.Escape), dialog => dialog.Close() })
        {
            Assert.AreEqual(MessageBoxResult.OK, Respond(
                () => AppMessageDialog.Show(owner, "合成错误", "单按钮关闭", MessageBoxButton.OK, MessageBoxImage.Error), dismiss));
        }
        foreach (var buttons in new[] { MessageBoxButton.YesNo, MessageBoxButton.YesNoCancel })
        {
            var safeResult = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
            foreach (var key in new[] { Key.Enter, Key.Escape })
            {
                Assert.AreEqual(safeResult, Respond(() => AppMessageDialog.Show(owner, "操作确认", "默认取消", buttons, MessageBoxImage.Warning), dialog =>
                {
                    Assert.IsTrue(Find<Button>(dialog, "CancelActionButton").IsKeyboardFocused);
                    Press(dialog, key);
                }));
            }
            Assert.AreEqual(safeResult, Respond(() => AppMessageDialog.Show(owner, "操作确认", "关闭取消", buttons, MessageBoxImage.Warning), dialog => dialog.Close()));
            Assert.AreEqual(MessageBoxResult.Yes, Respond(() => AppMessageDialog.Show(owner, "操作确认", "键盘确认", buttons, MessageBoxImage.Question), dialog =>
            {
                Find<TextBox>(dialog, "MessageBox").Focus();
                Press(dialog, Key.Enter);
                Assert.IsTrue(dialog.IsVisible, "正文中的 Enter 不能确认操作。");
                Find<Button>(dialog, "PrimaryActionButton").Focus();
                Press(dialog, Key.Enter);
            }));
        }
        Assert.AreEqual(MessageBoxResult.No, Respond(() => AppMessageDialog.Show(owner, "导入选择", "保留本机", MessageBoxButton.YesNoCancel, MessageBoxImage.Question), dialog =>
        {
            Find<Button>(dialog, "NegativeActionButton").Focus();
            Press(dialog, Key.Enter);
        }));
        Assert.AreEqual(MessageBoxResult.OK, Respond(() => AppMessageDialog.Show("合成启动参数错误", "无主窗口提示", MessageBoxButton.OK, MessageBoxImage.Error), dialog =>
        {
            Assert.IsNull(dialog.Owner);
            Assert.IsTrue(dialog.ShowInTaskbar);
            Click(dialog, "PrimaryActionButton");
        }));
    }

    private static void CheckLayout(Window owner)
    {
        Stage = "长消息、小窗口与缩放布局";
        var longMessage = string.Join("\n", Enumerable.Range(1, 80).Select(index =>
            $"第 {index} 行合成错误：请检查配置。D:\\Placeholder\\{new string('x', 150)}\\workspace.json"));
        Respond(() => AppMessageDialog.Show(owner, longMessage, "合成导入冲突与多行错误", MessageBoxButton.YesNoCancel, MessageBoxImage.Question), dialog =>
        {
            Assert.IsLessThanOrEqualTo(dialog.MaxHeight + 1, dialog.ActualHeight);
            var message = Find<TextBox>(dialog, "MessageBox");
            message.Select(0, 12);
            Assert.AreEqual(longMessage[..12], message.SelectedText);
            string? copiedText = null;
            DataObject.AddCopyingHandler(message, (_, args) =>
            {
                copiedText = args.DataObject.GetData(DataFormats.UnicodeText) as string;
                args.CancelCommand(); // 验证复制数据，不改变用户的系统剪贴板。
            });
            ApplicationCommands.Copy.Execute(null, message);
            Assert.AreEqual(message.SelectedText, copiedText);
            Assert.IsGreaterThan(message.ViewportHeight, message.ExtentHeight, "长内容应能滚动。");
            message.ScrollToEnd();
            Capture(dialog, "message-long");
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.Width = 380;
            dialog.Height = 320;
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                ((FrameworkElement)dialog.Content).LayoutTransform = new ScaleTransform(scale, scale);
                // 缩放测试同时提供相同比例的逻辑空间，检查所有按钮和滚动区域。
                dialog.Width = Math.Min(380 * scale, dialog.MaxWidth);
                dialog.Height = Math.Min(320 * scale, dialog.MaxHeight);
                dialog.UpdateLayout();
                AssertVisibleButtons(dialog);
                Assert.IsGreaterThan(message.ViewportHeight, message.ExtentHeight);
                Capture(dialog, "message-small-scale-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            Click(dialog, "CancelActionButton");
        });
    }

    private static void CheckEditorValidation(Window owner)
    {
        Stage = "编辑校验与焦点返回";
        var editor = new ProjectEditorWindow { Owner = owner };
        try
        {
            editor.Show();
            Pump();
            var name = Find<TextBox>(editor, "NameBox");
            name.Focus();
            Respond(() => { Click(editor, "SaveButton"); return true; }, dialog =>
            {
                Assert.AreSame(editor, dialog.Owner);
                Assert.AreEqual("项目信息不完整", Find<TextBlock>(dialog, "HeadingText").Text);
                Press(dialog, Key.Escape);
            });
            Assert.IsNull(editor.Result);
            Assert.IsTrue(editor.IsVisible);
            Assert.IsTrue(name.IsKeyboardFocused);
        }
        finally { editor.Close(); }
    }

    private static void CheckImportChoices(MainWindow window, string root)
    {
        Stage = "三类导入冲突的替换、保留与取消";
        var local = window.Projects.Single();
        var marker = Path.Combine(root, "must-not-run.txt");
        var incomingCommand = new CommandDefinition
        {
            Name = local.Commands[0].Name, WorkingDirectory = root, Purpose = "导入用途",
            Command = $"Set-Content -LiteralPath '{marker}' -Value 'unexpected'"
        };
        var incoming = new WorkspaceConfiguration
        {
            Projects = [new()
            {
                Name = "导入项目", Directory = root, Commands = [incomingCommand],
                Groups = [new() { Name = local.Groups[0].Name, ExecutionMode = CommandGroupExecutionMode.Sequential, CommandIds = [incomingCommand.Id] }]
            }]
        };
        var preview = (string)Invoke(window, "BuildImportPreview", incoming)!;
        StringAssert.Contains(preview, "不会自动运行命令或连接探测端口");
        foreach (var result in new[] { MessageBoxResult.No, MessageBoxResult.Yes })
        {
            var target = new List<ProjectDefinition> { (ProjectDefinition)InvokeStatic("CloneProject", local)! };
            Assert.IsTrue(Respond(() => (bool)Invoke(window, "MergeImportedWorkspace", incoming, target)!,
                Conflict("项目已存在", result), Conflict("命令名称冲突", result), Conflict("分组名称冲突", result)));
            Assert.AreEqual(result == MessageBoxResult.Yes ? "导入项目" : local.Name, target[0].Name);
            Assert.AreEqual(result == MessageBoxResult.Yes ? incomingCommand.Command : local.Commands[0].Command, target[0].Commands[0].Command);
            Assert.AreEqual(result == MessageBoxResult.Yes ? CommandGroupExecutionMode.Sequential : local.Groups[0].ExecutionMode, target[0].Groups[0].ExecutionMode);
            Assert.AreEqual(local.Commands[0].Id, target[0].Groups[0].CommandIds[0]);
        }
        var titles = new[] { "项目已存在", "命令名称冲突", "分组名称冲突" };
        for (var cancelAt = 0; cancelAt < titles.Length; cancelAt++)
        {
            var target = new List<ProjectDefinition> { (ProjectDefinition)InvokeStatic("CloneProject", local)! };
            var responses = titles.Take(cancelAt + 1).Select((title, index) =>
                Conflict(title, index == cancelAt ? MessageBoxResult.Cancel : MessageBoxResult.Yes)).ToArray();
            Assert.IsFalse(Respond(() => (bool)Invoke(window, "MergeImportedWorkspace", incoming, target)!, responses));
            Assert.AreEqual("隔离提示项目", window.Projects.Single().Name, "取消不能修改实际工作区。");
        }
        Assert.IsFalse(File.Exists(marker));
        Assert.IsEmpty(Sessions(window));
    }

    private static Action<AppMessageDialogWindow> Conflict(string title, MessageBoxResult result) => dialog =>
    {
        Assert.AreEqual(title, Find<TextBlock>(dialog, "HeadingText").Text);
        Assert.AreEqual("取消导入", Find<Button>(dialog, "CancelActionButton").Content);
        Assert.AreEqual("保留本机", Find<Button>(dialog, "NegativeActionButton").Content);
        Assert.AreEqual("替换", Find<Button>(dialog, "PrimaryActionButton").Content);
        Click(dialog, result switch
        {
            MessageBoxResult.Yes => "PrimaryActionButton",
            MessageBoxResult.No => "NegativeActionButton",
            _ => "CancelActionButton"
        });
    };

    private static void CheckDamagedLogHistory(MainWindow window)
    {
        Stage = "损坏日志的清理确认、取消与运行中保护";
        Find<ListBox>(window, "ProjectList").SelectedIndex = 0;
        Click(window, "CommandsPageButton");
        Find<ListBox>(window, "CommandList").SelectedIndex = 0;
        Click(window, "OutputPageButton");
        Find<Expander>(window, "LogSettingsExpander").IsExpanded = true;
        var logs = (RunLogStore)typeof(MainWindow).GetField("_runLogStore", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var project = window.Projects.Single();
        var command = project.Commands.Single();
        var old = logs.Begin(project.Id, command.Id, DateTimeOffset.Now.AddMinutes(-2));
        logs.Append(old, new CommandOutput(DateTimeOffset.Now, "stdout", "damaged history"));
        logs.Complete(old, new CommandRunResult(CommandRunState.Succeeded, 0, false));
        var oldDirectory = GetDirectory(old);
        File.WriteAllText(Path.Combine(oldDirectory, "run.json"), "{");
        var active = logs.Begin(project.Id, command.Id, DateTimeOffset.Now);
        var activeDirectory = GetDirectory(active);
        try
        {
            File.WriteAllText(Path.Combine(activeDirectory, "run.json"), "{");
            Respond(() => { Click(window, "ClearHistoryButton"); return true; }, dialog =>
            {
                StringAssert.Contains(Find<TextBox>(dialog, "MessageBox").Text, "元数据损坏或缺失的批次也会清理");
                Assert.IsTrue(Find<Button>(dialog, "CancelActionButton").IsKeyboardFocused);
                Press(dialog, Key.Escape);
            });
            Assert.IsTrue(Directory.Exists(oldDirectory));
            Assert.IsTrue(Directory.Exists(activeDirectory));
            Respond(() => { Click(window, "ClearHistoryButton"); return true; }, dialog => Click(dialog, "PrimaryActionButton"));
            Assert.IsFalse(Directory.Exists(oldDirectory));
            Assert.IsTrue(Directory.Exists(activeDirectory));
            Assert.AreEqual("已清理历史日志。", Find<TextBlock>(window, "LogStatusText").Text);
            Assert.HasCount(1, Find<ComboBox>(window, "RunHistoryComboBox").Items);
            logs.Append(active, new CommandOutput(DateTimeOffset.Now, "stdout", "still active"));
            Assert.IsFalse(active.Info.WriteFailed);
        }
        finally { logs.Complete(active, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }

        string GetDirectory(RunLogWriter writer) => Path.Combine(logs.RootPath, writer.Info.ProjectId.ToString("N"),
            writer.Info.CommandId.ToString("N"), writer.Info.RunId.ToString("N"));
    }

    private static void CheckDeletion(MainWindow window, WorkspaceStore store)
    {
        Stage = "删除取消、确认及保存";
        Find<ListBox>(window, "ProjectList").SelectedIndex = 0;
        Click(window, "CommandsPageButton");
        Find<ListBox>(window, "CommandList").SelectedIndex = 0;
        Respond(() => { Click(window, "DeleteCommandButton"); return true; }, dialog => Press(dialog, Key.Escape));
        Assert.HasCount(1, window.Projects.Single().Commands);
        Respond(() => { Click(window, "DeleteCommandButton"); return true; }, dialog =>
        {
            Assert.AreEqual("删除", Find<Button>(dialog, "PrimaryActionButton").Content);
            Assert.IsTrue(Find<Button>(dialog, "CancelActionButton").IsKeyboardFocused);
            Click(dialog, "PrimaryActionButton");
        });
        Wait(() => Find<Button>(window, "ImportButton").IsEnabled);
        Assert.HasCount(0, window.Projects.Single().Commands);
        Assert.HasCount(0, store.LoadAsync().GetAwaiter().GetResult().Projects.Single().Commands);
    }

    private static void CheckExitInteraction(Window owner)
    {
        Stage = "退出取消、确认及停止失败提示";
        var interaction = new MainWindowExitInteraction();
        Assert.IsFalse(Respond(() => interaction.ConfirmExit(owner, 1, 1), dialog =>
        {
            Assert.AreEqual("返回应用", Find<Button>(dialog, "CancelActionButton").Content);
            Assert.AreEqual("停止并退出", Find<Button>(dialog, "PrimaryActionButton").Content);
            Press(dialog, Key.Enter);
        }));
        Assert.IsTrue(Respond(() => interaction.ConfirmExit(owner, 1, 0), dialog => Click(dialog, "PrimaryActionButton")));
        Respond(() => { interaction.ReportFailure(owner, new IOException("合成停止失败")); return true; }, dialog =>
        {
            Assert.AreEqual("仍有命令未停止", Find<TextBlock>(dialog, "HeadingText").Text);
            StringAssert.Contains(Find<TextBox>(dialog, "MessageBox").Text, "合成停止失败");
            Press(dialog, Key.Escape);
        });
        Assert.IsTrue(owner.IsVisible);
    }

    private static void CheckRunningExit(string root)
    {
        Stage = "运行中实际关闭、取消、停止失败与重试";
        var store = new WorkspaceStore(Path.Combine(root, "exit-workspace.json"));
        var command = new CommandDefinition
        {
            Name = "退出合成服务", WorkingDirectory = root, Kind = CommandKind.Service,
            Command = "Write-Output 'dialog-exit-ready'; while ($true) { Start-Sleep -Milliseconds 100 }"
        };
        store.SaveAsync(new() { Projects = [new() { Name = "退出隔离项目", Directory = root, Commands = [command] }] }).GetAwaiter().GetResult();
        var interaction = new ExitInteraction();
        var window = new MainWindow(store, "隔离退出提示验收", null, null, null,
            trayIcon: new TestTrayIcon(), exitInteraction: interaction);
        try
        {
            window.Show();
            Wait(() => Find<Button>(window, "ManualUpdateCheckButton").IsEnabled);
            Find<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "CommandsPageButton");
            Find<ListBox>(window, "CommandList").SelectedIndex = 0;
            Click(window, "RunButton");
            Wait(() => Find<TextBox>(window, "OutputTextBox").Text.Contains("dialog-exit-ready"));
            var session = (CommandRunSession)Sessions(window)[command.Id]!;
            CheckInvalidCommandStart(window, session, root);
            Stage = "运行中实际关闭、取消、停止失败与重试";
            Respond(() => { window.Close(); return true; }, dialog => Press(dialog, Key.Enter));
            Assert.IsTrue(window.IsVisible);
            Assert.IsFalse(session.Completion.IsCompleted);
            interaction.FailStop = true;
            Respond(() => { window.Close(); Wait(() => interaction.FailureReported); return true; },
                dialog => Click(dialog, "PrimaryActionButton"),
                dialog =>
                {
                    Assert.AreEqual("仍有命令未停止", Find<TextBlock>(dialog, "HeadingText").Text);
                    Click(dialog, "PrimaryActionButton");
                });
            Assert.IsTrue(window.IsVisible);
            Assert.IsFalse(session.Completion.IsCompleted);
            interaction.FailStop = false;
            Respond(() => { window.Close(); return true; }, dialog => Click(dialog, "PrimaryActionButton"));
            Wait(() => session.Completion.IsCompleted && !window.IsVisible);
            Assert.IsFalse(File.Exists(Path.Combine(root, "empty-id-started.txt")));
        }
        finally
        {
            interaction.SkipConfirmation = true;
            interaction.FailStop = false;
            if (window.IsVisible)
            {
                window.Close();
                Wait(() => !window.IsVisible);
            }
        }
    }

    private static void CheckInvalidWorkspaceIds(string root)
    {
        Stage = "全零项目和命令 ID 的桌面加载错误";
        foreach (var emptyProjectId in new[] { true, false })
        {
            var path = Path.Combine(root, emptyProjectId ? "empty-project-id.json" : "empty-command-id.json");
            var command = new CommandDefinition { Name = "异常配置任务", Command = "Write-Output 'unexpected'", WorkingDirectory = root };
            var project = new ProjectDefinition { Name = "异常配置项目", Directory = root, Commands = [command] };
            if (emptyProjectId) project.Id = Guid.Empty;
            else command.Id = Guid.Empty;
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new WorkspaceConfiguration { Projects = [project] });
            File.WriteAllBytes(path, bytes);
            var window = new MainWindow(new WorkspaceStore(path), "隔离异常配置验收", null, null, null, trayIcon: new TestTrayIcon());
            try
            {
                Respond(() =>
                {
                    window.Show();
                    Wait(() => Find<Button>(window, "ManualUpdateCheckButton").IsEnabled);
                    return true;
                }, dialog =>
                {
                    Assert.AreEqual("无法加载工作区", Find<TextBlock>(dialog, "HeadingText").Text);
                    StringAssert.Contains(Find<TextBox>(dialog, "MessageBox").Text, emptyProjectId ? "项目 ID" : "命令 ID");
                    Click(dialog, "PrimaryActionButton");
                });
                Assert.IsTrue(window.IsVisible);
                Assert.IsEmpty(window.Projects);
                Assert.IsEmpty(Sessions(window));
                Assert.IsFalse((bool)typeof(MainWindow).GetField("_canSave", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
                Assert.IsFalse(Find<Button>(window, "RunButton").IsEnabled);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
                Assert.HasCount(0, Directory.GetFiles(path + ".logs", "run.json", SearchOption.AllDirectories));
            }
            finally
            {
                window.Close();
                Pump();
            }
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }
    }

    private static void CheckInvalidCommandStart(MainWindow window, CommandRunSession runningSession, string root)
    {
        Stage = "全零 ID 启动失败且不影响已有服务";
        var metadataFiles = Directory.GetFiles(root, "run.json", SearchOption.AllDirectories);
        foreach (var emptyProjectId in new[] { true, false })
        {
            var command = new CommandDefinition
            {
                Name = "异常 ID 任务", WorkingDirectory = root,
                Command = "Set-Content 'empty-id-started.txt' 'unexpected'"
            };
            var project = new ProjectDefinition { Name = "异常 ID 项目", Directory = root, Commands = [command] };
            if (emptyProjectId) project.Id = Guid.Empty;
            else command.Id = Guid.Empty;
            var pending = (Task<(ICommandRunSession? Session, string Error)>)typeof(MainWindow)
                .GetMethod("StartCommandSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [project, command, CommandStartSource.Independent, false, false, CancellationToken.None])!;
            Wait(() => pending.IsCompleted);
            var result = pending.GetAwaiter().GetResult();
            Assert.IsNull(result.Session);
            StringAssert.Contains(result.Error, "ID 不能为空");
            Assert.HasCount(1, Sessions(window));
            Assert.AreSame(runningSession, Sessions(window).Values.Cast<CommandRunSession>().Single());
            Assert.IsFalse(runningSession.Completion.IsCompleted);
            CollectionAssert.AreEquivalent(metadataFiles, Directory.GetFiles(root, "run.json", SearchOption.AllDirectories));
            Assert.IsFalse(File.Exists(Path.Combine(root, "empty-id-started.txt")));
        }
    }

    private sealed class ExitInteraction : IMainWindowExitInteraction
    {
        private readonly MainWindowExitInteraction _desktop = new();
        internal bool FailStop, SkipConfirmation, FailureReported;
        public bool ConfirmExit(Window owner, int commandCount, int sequenceCount, int pendingCount = 0) =>
            SkipConfirmation || _desktop.ConfirmExit(owner, commandCount, sequenceCount, pendingCount);
        public Task StopAsync(CommandRunSession session) =>
            FailStop ? Task.FromException(new IOException("合成停止失败")) : session.StopAsync();
        public void ReportFailure(Window owner, Exception exception)
        {
            _desktop.ReportFailure(owner, exception);
            FailureReported = true;
        }
    }

    private static T Respond<T>(Func<T> action, params Action<AppMessageDialogWindow>[] responses)
    {
        var pending = new Queue<Action<AppMessageDialogWindow>>(responses);
        var expected = pending.Count;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<AppMessageDialogWindow>().FirstOrDefault(window => window.IsVisible && window.IsLoaded);
            if (dialog is null) return;
            try
            {
                var detail = $"Title={dialog.Title}; Heading={Find<TextBlock>(dialog, "HeadingText").Text}; " +
                    $"Elapsed={clock.Elapsed.TotalSeconds:F3}s; Limit=10s; Handled={expected - pending.Count}; Remaining={pending.Count}; Stage={Stage}";
                if (pending.Count == 0) Assert.Fail($"出现额外提示：{detail}");
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail($"预期提示响应超时：{detail}");
                pending.Dequeue()(dialog);
            }
            catch (Exception exception)
            {
                failure = exception;
                dialog.Close();
            }
        };
        timer.Start();
        try
        {
            var result = action();
            if (failure is not null) throw new AssertFailedException($"提示检查失败（{Stage}）：{failure}");
            Assert.HasCount(0, pending, $"预期提示未出现；阶段：{Stage}。");
            return result;
        }
        finally { timer.Stop(); }
    }

    private static void AssertVisibleButtons(Window window)
    {
        foreach (var name in new[] { "CancelActionButton", "NegativeActionButton", "PrimaryActionButton" })
        {
            var button = Find<Button>(window, name);
            if (!button.IsVisible) continue;
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            Assert.IsTrue(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight,
                $"按钮 {button.Content} 被裁切：{bounds} / {window.ActualWidth}×{window.ActualHeight}。");
        }
    }

    private static void Press(Window window, Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static T Find<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static object? InvokeStatic(string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static System.Collections.IDictionary Sessions(MainWindow window) =>
        (System.Collections.IDictionary)typeof(MainWindow).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"提示回归等待超时：{Stage}。");
            Pump();
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr handle);
}
