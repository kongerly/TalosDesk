using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App;

public partial class MainWindow : Window
{
    private readonly WorkspaceStore _store = new();
    private readonly CommandRunner _runner = new();
    private readonly Dictionary<Guid, CommandRunSession> _sessions = [];
    private readonly Dictionary<Guid, CommandRunResult> _lastResults = [];
    private readonly Dictionary<Guid, DateTimeOffset> _runStartedAt = [];
    private readonly Dictionary<Guid, long> _runVersions = [];
    private readonly HashSet<Guid> _restartsInProgress = [];
    private readonly Dictionary<Guid, ObservableCollection<CommandOutput>> _logs = [];
    private readonly BoundedOutputInbox _pendingOutput = new(10_000);
    private int _outputFlushScheduled;
    private bool _canSave = true;
    private bool _saveFailed;
    private bool _workspaceChangeInProgress;
    private bool _allowClose;
    private bool _isStoppingForClose;
    private ProjectDefinition? _selectedProject;
    private CommandDefinition? _selectedCommand;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<ProjectDefinition> Projects { get; } = [];

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var configuration = await _store.LoadAsync();
            foreach (var project in configuration.Projects)
            {
                project.Commands ??= [];
                Projects.Add(project);
            }

            if (Projects.Count > 0) ProjectList.SelectedIndex = 0;
            UpdateEmptyStates();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            _canSave = false;
            RefreshCommandSelection();
            MessageBox.Show(this,
                $"TalosDesk 无法读取本机工作区配置，原文件未作修改。\n\n{exception.Message}",
                "无法加载工作区", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProjectList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedProject = ProjectList.SelectedItem as ProjectDefinition;
        _selectedCommand = null;
        CommandList.ItemsSource = _selectedProject?.Commands;
        CommandList.SelectedIndex = -1;
        ProjectNameText.Text = _selectedProject?.Name ?? "请选择项目";
        ProjectPathText.Text = _selectedProject?.Directory ?? "添加本地文件夹以开始使用";
        RefreshCommandSelection();
        UpdateEmptyStates();
    }

    private void CommandList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedCommand = CommandList.SelectedItem as CommandDefinition;
        RefreshCommandSelection();
    }

    private void RefreshCommandSelection()
    {
        var command = _selectedCommand;
        var running = command is not null && _sessions.TryGetValue(command.Id, out var session) && !session.Completion.IsCompleted;
        var restarting = command is not null && _restartsInProgress.Contains(command.Id);
        var canChangeWorkspace = !_workspaceChangeInProgress && _restartsInProgress.Count == 0 && !_isStoppingForClose && _canSave;
        AddProjectButton.IsEnabled = canChangeWorkspace;
        ImportButton.IsEnabled = canChangeWorkspace;
        ExportButton.IsEnabled = canChangeWorkspace;
        AddCommandButton.IsEnabled = _selectedProject is not null && canChangeWorkspace;
        EditProjectButton.IsEnabled = _selectedProject is not null && canChangeWorkspace;
        ProjectNameText.Text = _selectedProject?.Name ?? "请选择项目";
        ProjectPathText.Text = _selectedProject?.Directory ?? "添加本地文件夹以开始使用";
        CommandCountText.Text = _selectedProject?.Commands.Count.ToString() ?? "0";
        SelectedCommandName.Text = command?.Name ?? "请选择命令";
        RunStartedText.Text = command is not null && _runStartedAt.TryGetValue(command.Id, out var startedAt)
            ? $"开始时间：{startedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : string.Empty;
        LogCommandName.Text = command is null ? "选择命令以查看输出" : $"  {command.Name}";
        RunButton.IsEnabled = command is not null && !running && !restarting && !_workspaceChangeInProgress && !_isStoppingForClose && _canSave;
        StopButton.IsEnabled = command is not null && running && !restarting && !_isStoppingForClose;
        RestartButton.IsEnabled = command is not null && !restarting && !_workspaceChangeInProgress && !_isStoppingForClose && _canSave;
        EditButton.IsEnabled = command is not null && !running && canChangeWorkspace;
        CopyButton.IsEnabled = command is not null;
        SaveStatusText.Text = !_canSave ? "配置错误" : _workspaceChangeInProgress ? "正在处理…" : _saveFailed ? "保存失败" : "本机配置";

        if (command is null)
        {
            RunStateText.Text = "空闲";
            RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            OutputList.ItemsSource = null;
            EmptyOutputHint.Visibility = Visibility.Visible;
            return;
        }

        OutputList.ItemsSource = GetLogs(command.Id);
        EmptyOutputHint.Visibility = GetLogs(command.Id).Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            RunStateText.Text = "运行中";
            RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else if (_lastResults.TryGetValue(command.Id, out var result))
        {
            RunStateText.Text = result.State == CommandRunState.Succeeded
                ? $"已成功 · 退出码 {result.ExitCode}"
                : result.State == CommandRunState.Stopped
                    ? $"{(result.WasForceTerminated ? "已强制停止" : "已停止")} · 退出码 {result.ExitCode}"
                    : $"失败 · 退出码 {result.ExitCode}";
            RunStateText.Foreground = result.State == CommandRunState.Succeeded ? System.Windows.Media.Brushes.SeaGreen : (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else
        {
            RunStateText.Text = "空闲";
            RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        }

        if (GetLogs(command.Id).Count > 0) OutputList.ScrollIntoView(GetLogs(command.Id)[^1]);
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceChangeInProgress || !_canSave) return;
        var editor = new ProjectEditorWindow { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var project = editor.Result;
        if (Projects.Any(existing => SameDirectory(existing.Directory, project.Directory)))
        {
            MessageBox.Show(this, "已有项目使用此文件夹。请在项目列表中选择该项目并编辑信息。", "项目文件夹已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginWorkspaceChange()) return;
        try
        {
            Projects.Add(project);
            ProjectList.SelectedItem = project;
            if (!await SaveWorkspaceAsync())
            {
                Projects.Remove(project);
                RefreshProjectList();
                UpdateEmptyStates();
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private async void EditProject_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceChangeInProgress || !_canSave || _selectedProject is null) return;
        var project = _selectedProject;
        if (HasRunningCommands(project))
        {
            MessageBox.Show(this, "请先停止此项目中正在运行的命令，再编辑项目信息，以避免运行目录与保存配置不一致。", "项目命令仍在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = new ProjectEditorWindow(project) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (Projects.Any(existing => existing.Id != project.Id && SameDirectory(existing.Directory, editor.Result.Directory)))
        {
            MessageBox.Show(this, "另一个项目已使用此文件夹，请选择其他文件夹。", "项目文件夹已被使用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginWorkspaceChange()) return;
        try
        {
            var previousName = project.Name;
            var previousDirectory = project.Directory;
            var previousWorkingDirectories = project.Commands.ToDictionary(command => command.Id, command => command.WorkingDirectory);
            project.Name = editor.Result.Name;
            project.Directory = editor.Result.Directory;
            if (!SameDirectory(previousDirectory, project.Directory))
            {
                foreach (var command in project.Commands)
                {
                    command.WorkingDirectory = RelocateWorkingDirectory(previousDirectory, project.Directory, command.WorkingDirectory);
                }
            }

            RefreshProjectList();
            if (!await SaveWorkspaceAsync())
            {
                project.Name = previousName;
                project.Directory = previousDirectory;
                foreach (var command in project.Commands) command.WorkingDirectory = previousWorkingDirectories[command.Id];
                RefreshProjectList();
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private static string RelocateWorkingDirectory(string previousProjectDirectory, string newProjectDirectory, string workingDirectory)
    {
        var relativePath = Path.GetRelativePath(previousProjectDirectory, Path.GetFullPath(workingDirectory));
        if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return workingDirectory;
        }

        return Path.GetFullPath(Path.Combine(newProjectDirectory, relativePath));
    }

    private async void ExportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceChangeInProgress || !_canSave) return;
        var picker = new SaveFileDialog
        {
            Title = "导出 TalosDesk 工作区",
            Filter = "TalosDesk 工作区 (*.talosdesk.json)|*.talosdesk.json|JSON 文件 (*.json)|*.json",
            FileName = "talosdesk-workspace.talosdesk.json",
            AddExtension = true,
            DefaultExt = ".talosdesk.json"
        };
        if (picker.ShowDialog(this) != true) return;

        if (!BeginWorkspaceChange()) return;
        try
        {
            await WorkspaceStore.WriteFileAsync(picker.FileName, new WorkspaceConfiguration { Projects = Projects.ToList() });
            SaveStatusText.Text = "工作区已导出";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"TalosDesk 无法导出此工作区。\n\n{exception.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndWorkspaceChange(); }
    }

    private async void ImportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceChangeInProgress || !_canSave) return;
        if (_restartsInProgress.Count > 0 || _sessions.Values.Any(session => !session.Completion.IsCompleted))
        {
            MessageBox.Show(this, "导入工作区前请先停止所有正在运行的命令，以免运行中的命令与保存的配置不一致。", "命令仍在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = "导入 TalosDesk 工作区",
            Filter = "TalosDesk 工作区 (*.talosdesk.json;*.json)|*.talosdesk.json;*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        if (!BeginWorkspaceChange()) return;
        try
        {
            WorkspaceConfiguration imported;
            try
            {
                imported = await WorkspaceStore.ReadFileAsync(picker.FileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                MessageBox.Show(this, $"TalosDesk 无法读取此工作区文件，未作任何更改。\n\n{exception.Message}", "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var preview = BuildImportPreview(imported);
            if (MessageBox.Show(this, preview, "确认导入工作区", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;

            var previousProjects = Projects.ToList();
            var applied = false;
            try
            {
                var mergedProjects = Projects.Select(CloneProject).ToList();
                if (!MergeImportedWorkspace(imported, mergedProjects)) return;
                applied = true;
                ReplaceProjects(mergedProjects);
                if (!await SaveWorkspaceAsync())
                {
                    ReplaceProjects(previousProjects);
                    return;
                }

                SaveStatusText.Text = "工作区已导入";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (applied) ReplaceProjects(previousProjects);
                MessageBox.Show(this, $"TalosDesk 无法保存导入的工作区。\n\n{exception.Message}", "未能保存导入内容", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private void ReplaceProjects(IEnumerable<ProjectDefinition> projects)
    {
        Projects.Clear();
        foreach (var project in projects) Projects.Add(project);
        RefreshProjectList();
        UpdateEmptyStates();
    }

    private string BuildImportPreview(WorkspaceConfiguration imported)
    {
        var matchingProjects = imported.Projects.Count(incoming => Projects.Any(existing => SameDirectory(existing.Directory, incoming.Directory)));
        var projectDetails = imported.Projects.Sum(project => project.Commands.Count(command =>
            Projects.FirstOrDefault(existing => SameDirectory(existing.Directory, project.Directory))?.Commands.Any(existing => SameCommandName(existing.Name, command.Name)) == true));
        var newProjects = imported.Projects.Count - matchingProjects;
        var newCommands = imported.Projects.Sum(project =>
        {
            var existing = Projects.FirstOrDefault(candidate => SameDirectory(candidate.Directory, project.Directory));
            return existing is null ? project.Commands.Count : project.Commands.Count(command => !existing.Commands.Any(current => SameCommandName(current.Name, command.Name)));
        });

        var visibleProjects = imported.Projects.Take(12).Select(project =>
        {
            var commands = project.Commands.Take(8).Select(command => $"    - {command.Name}: {command.Command}");
            var omittedCommands = project.Commands.Count > 8 ? $"\n    … 另有 {project.Commands.Count - 8} 条命令" : string.Empty;
            return $"• {project.Name}\n  {project.Directory}\n{string.Join("\n", commands)}{omittedCommands}";
        });
        var omittedProjects = imported.Projects.Count > 12 ? $"\n… 另有 {imported.Projects.Count - 12} 个项目" : string.Empty;

        return $"即将导入 {imported.Projects.Count} 个项目、{imported.Projects.Sum(project => project.Commands.Count)} 条命令。\n" +
               $"新增项目：{newProjects} · 文件夹相同：{matchingProjects}\n" +
               $"新增命令：{newCommands} · 名称冲突：{projectDetails}\n\n" +
               $"{string.Join("\n\n", visibleProjects)}{omittedProjects}\n\n" +
               "导入不会自动运行任何命令。遇到同一项目或同名命令时，你可以选择保留本机版本或替换为导入版本。是否继续？";
    }

    private bool MergeImportedWorkspace(WorkspaceConfiguration imported, List<ProjectDefinition> targetProjects)
    {
        foreach (var incomingProject in imported.Projects)
        {
            var existingProject = targetProjects.FirstOrDefault(project => SameDirectory(project.Directory, incomingProject.Directory));
            if (existingProject is null)
            {
                var addedProject = CloneProject(incomingProject);
                if (targetProjects.Any(project => project.Id == addedProject.Id)) addedProject.Id = Guid.NewGuid();
                foreach (var command in addedProject.Commands)
                {
                    if (targetProjects.SelectMany(project => project.Commands).Any(existing => existing.Id == command.Id)) command.Id = Guid.NewGuid();
                }

                targetProjects.Add(addedProject);
                continue;
            }

            var projectChoice = MessageBox.Show(this,
                $"已有项目使用此文件夹：\n{existingProject.Directory}\n\n本机名称：{existingProject.Name}\n导入名称：{incomingProject.Name}\n\n选择“是”以替换项目信息，选择“否”以保留本机信息，选择“取消”以停止导入。",
                "项目已存在", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (projectChoice == MessageBoxResult.Cancel) return false;
            if (projectChoice == MessageBoxResult.Yes)
            {
                existingProject.Name = incomingProject.Name;
                existingProject.Directory = Path.GetFullPath(incomingProject.Directory);
            }

            foreach (var incomingCommand in incomingProject.Commands)
            {
                var existingCommandIndex = existingProject.Commands.FindIndex(command => SameCommandName(command.Name, incomingCommand.Name));
                if (existingCommandIndex < 0)
                {
                    var addedCommand = CloneCommand(incomingCommand);
                    if (targetProjects.SelectMany(project => project.Commands).Any(existing => existing.Id == addedCommand.Id)) addedCommand.Id = Guid.NewGuid();
                    existingProject.Commands.Add(addedCommand);
                    continue;
                }

                var existingCommand = existingProject.Commands[existingCommandIndex];
                var commandChoice = MessageBox.Show(this,
                    $"项目“{existingProject.Name}”中已有同名命令。\n\n名称：{existingCommand.Name}\n本机命令：{existingCommand.Command}\n导入命令：{incomingCommand.Command}\n\n选择“是”以替换命令，选择“否”以保留本机命令，选择“取消”以停止导入。",
                    "命令名称冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (commandChoice == MessageBoxResult.Cancel) return false;
                if (commandChoice == MessageBoxResult.Yes)
                {
                    var replacement = CloneCommand(incomingCommand);
                    replacement.Id = existingCommand.Id;
                    existingProject.Commands[existingCommandIndex] = replacement;
                }
            }
        }

        return true;
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private bool HasRunningCommands(ProjectDefinition project) => project.Commands.Any(command =>
        _restartsInProgress.Contains(command.Id) ||
        _sessions.TryGetValue(command.Id, out var session) && !session.Completion.IsCompleted);

    private static bool SameCommandName(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static ProjectDefinition CloneProject(ProjectDefinition project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Directory = Path.GetFullPath(project.Directory),
        Commands = project.Commands.Select(CloneCommand).ToList()
    };

    private static CommandDefinition CloneCommand(CommandDefinition command) => new()
    {
        Id = command.Id,
        Name = command.Name,
        Purpose = command.Purpose,
        Command = command.Command,
        WorkingDirectory = command.WorkingDirectory,
        Kind = command.Kind
    };

    private void RefreshProjectList()
    {
        var selectedId = _selectedProject?.Id;
        var selectedCommandId = _selectedCommand?.Id;
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = Projects;
        ProjectList.SelectedItem = Projects.FirstOrDefault(project => project.Id == selectedId);
        if (ProjectList.SelectedItem is null && Projects.Count > 0) ProjectList.SelectedIndex = 0;
        if (selectedCommandId is { } commandId)
        {
            CommandList.SelectedItem = _selectedProject?.Commands.FirstOrDefault(command => command.Id == commandId);
        }
    }

    private async void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceChangeInProgress || !_canSave || _selectedProject is null) return;
        var editor = new CommandEditorWindow(_selectedProject.Directory) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (_selectedProject.Commands.Any(command => SameCommandName(command.Name, editor.Result.Name)))
        {
            MessageBox.Show(this, "此项目已有同名命令，请使用其他名称。", "命令名称已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!BeginWorkspaceChange()) return;
        try
        {
            var project = _selectedProject;
            project.Commands.Add(editor.Result);
            CommandList.ItemsSource = null;
            CommandList.ItemsSource = project.Commands;
            CommandList.SelectedItem = editor.Result;
            UpdateEmptyStates();
            if (!await SaveWorkspaceAsync())
            {
                project.Commands.Remove(editor.Result);
                RefreshProjectList();
                UpdateEmptyStates();
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private async void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceChangeInProgress || !_canSave || _selectedProject is null || _selectedCommand is null) return;
        var editor = new CommandEditorWindow(_selectedProject.Directory, _selectedCommand) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (_selectedProject.Commands.Any(command => command.Id != editor.Result.Id && SameCommandName(command.Name, editor.Result.Name)))
        {
            MessageBox.Show(this, "此项目已有同名命令，请使用其他名称。", "命令名称已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!BeginWorkspaceChange()) return;
        try
        {
            var project = _selectedProject;
            var index = project.Commands.IndexOf(_selectedCommand);
            var previousCommand = project.Commands[index];
            project.Commands[index] = editor.Result;
            CommandList.ItemsSource = null;
            CommandList.ItemsSource = project.Commands;
            CommandList.SelectedItem = editor.Result;
            if (!await SaveWorkspaceAsync())
            {
                project.Commands[index] = previousCommand;
                RefreshProjectList();
                UpdateEmptyStates();
                return;
            }

            _logs.Remove(editor.Result.Id);
            _lastResults.Remove(editor.Result.Id);
            _runStartedAt.Remove(editor.Result.Id);
            RefreshCommandSelection();
            SaveStatusText.Text = "已保存到本机";
        }
        finally { EndWorkspaceChange(); }
    }

    private void RunCommand_Click(object sender, RoutedEventArgs e) => StartSelectedCommand();

    private async void RestartCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || _selectedCommand is null) return;
        var projectId = _selectedProject.Id;
        var commandId = _selectedCommand.Id;
        if (!_restartsInProgress.Add(commandId)) return;
        RefreshCommandSelection();
        try
        {
            if (_sessions.TryGetValue(commandId, out var session) && !session.Completion.IsCompleted)
            {
                RunStateText.Text = "正在重启 · 停止中";
                try
                {
                    await session.StopAsync();
                    await session.Completion;
                    if (_sessions.TryGetValue(commandId, out var current) && ReferenceEquals(current, session)) _sessions.Remove(commandId);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, $"TalosDesk 无法停止此命令，因此未执行重启。\n\n{exception.Message}", "重启已取消", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            var project = Projects.FirstOrDefault(candidate => candidate.Id == projectId);
            var command = project?.Commands.FirstOrDefault(candidate => candidate.Id == commandId);
            if (project is not null && command is not null) StartCommand(project, command);
        }
        finally
        {
            _restartsInProgress.Remove(commandId);
            RefreshCommandSelection();
        }
    }

    private void StartSelectedCommand()
    {
        if (_selectedProject is not null && _selectedCommand is not null) StartCommand(_selectedProject, _selectedCommand);
    }

    private void StartCommand(ProjectDefinition project, CommandDefinition command)
    {
        if (!_canSave || _workspaceChangeInProgress || _isStoppingForClose) return;
        var workingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? project.Directory : command.WorkingDirectory;
        try
        {
            var nextVersion = _runVersions.GetValueOrDefault(command.Id) + 1;
            var started = _runner.Start(command.Id, command.Command, workingDirectory,
                (_, output) => QueueOutput(command.Id, nextVersion, output));
            _runVersions[command.Id] = nextVersion;
            _lastResults.Remove(command.Id);
            _runStartedAt[command.Id] = DateTimeOffset.Now;
            GetLogs(command.Id).Clear();
            _sessions[command.Id] = started;
            RefreshCommandSelection();
            _ = CompleteRunAsync(command.Id, started);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            RefreshCommandSelection();
            MessageBox.Show(this, exception.Message, "命令启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CompleteRunAsync(Guid commandId, CommandRunSession session)
    {
        try
        {
            var result = await session.Completion;
            await Dispatcher.InvokeAsync(() =>
            {
                _lastResults[commandId] = result;
                if (_sessions.TryGetValue(commandId, out var current) && ReferenceEquals(current, session)) _sessions.Remove(commandId);
                if (_selectedCommand?.Id == commandId) RefreshCommandSelection();
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                RunStateText.Text = "进程异常";
                RunStateText.ToolTip = exception.Message;
            });
        }
    }

    private async void StopCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null || !_sessions.TryGetValue(_selectedCommand.Id, out var session)) return;
        var stoppedCommandId = _selectedCommand.Id;
        RunStateText.Text = "正在停止 · Ctrl+C";
        StopButton.IsEnabled = false;
        try
        {
            var stopResult = await session.StopAsync();
            await session.Completion;
            if (_selectedCommand?.Id == stoppedCommandId)
            {
                RunStateText.Text = stopResult == CommandStopResult.ForceTerminated ? "已强制停止" : "已停止";
                StopButton.IsEnabled = false;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"TalosDesk 无法确认命令已停止。\n\n{exception.Message}", "停止未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null) return;
        Clipboard.SetText(_selectedCommand.Command);
        SaveStatusText.Text = "命令已复制";
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null) return;
        GetLogs(_selectedCommand.Id).Clear();
        _pendingOutput.Clear(_selectedCommand.Id);
        EmptyOutputHint.Visibility = Visibility.Visible;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_workspaceChangeInProgress)
        {
            e.Cancel = true;
            MessageBox.Show(this, "工作区正在保存或导入，请等待操作完成后再退出。", "工作区正在处理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_restartsInProgress.Count > 0)
        {
            e.Cancel = true;
            MessageBox.Show(this, "命令正在重启，请等待停止与重新启动完成后再退出。", "命令正在重启", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var active = _sessions.Values.Where(session => !session.Completion.IsCompleted).ToArray();
        if (active.Length == 0) return;

        e.Cancel = true;
        var choice = MessageBox.Show(this,
            $"还有 {active.Length} 条命令正在运行。停止这些命令并退出吗？",
            "命令仍在运行", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return;

        _isStoppingForClose = true;
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        try
        {
            await Task.WhenAll(active.Select(session => session.StopAsync()));
            await Task.WhenAll(active.Select(session => session.Completion));
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _isStoppingForClose = false;
            RefreshCommandSelection();
            MessageBox.Show(this, $"TalosDesk 无法确认所有命令均已停止，因此窗口保持打开。\n\n{exception.Message}", "仍有命令未停止", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> SaveWorkspaceAsync()
    {
        if (!_canSave) return false;
        SaveStatusText.Text = "正在保存…";
        try
        {
            await _store.SaveAsync(new WorkspaceConfiguration { Projects = Projects.ToList() });
            _saveFailed = false;
            SaveStatusText.Text = "已保存到本机";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _saveFailed = true;
            SaveStatusText.Text = "保存失败";
            MessageBox.Show(this, $"TalosDesk 无法保存项目配置。\n\n{exception.Message}", "配置未保存", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool BeginWorkspaceChange()
    {
        if (!_canSave || _workspaceChangeInProgress || _restartsInProgress.Count > 0 || _isStoppingForClose) return false;
        _workspaceChangeInProgress = true;
        RefreshCommandSelection();
        return true;
    }

    private void EndWorkspaceChange()
    {
        var completedStatus = SaveStatusText.Text;
        _workspaceChangeInProgress = false;
        RefreshCommandSelection();
        if (!_saveFailed && (completedStatus is "已保存到本机" or "工作区已导入" or "工作区已导出"))
        {
            SaveStatusText.Text = completedStatus;
        }
    }

    private ObservableCollection<CommandOutput> GetLogs(Guid commandId)
    {
        if (!_logs.TryGetValue(commandId, out var logs)) _logs[commandId] = logs = [];
        return logs;
    }

    private void QueueOutput(Guid commandId, long runVersion, CommandOutput output)
    {
        _pendingOutput.Enqueue(commandId, runVersion, output);
        if (Interlocked.Exchange(ref _outputFlushScheduled, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingOutput));
        }
    }

    private void FlushPendingOutput()
    {
        CommandOutput? latestSelectedOutput = null;
        foreach (var entry in _pendingOutput.Take(500))
        {
            if (!_runVersions.TryGetValue(entry.CommandId, out var currentVersion) || currentVersion != entry.RunVersion) continue;
            AppendOutput(entry.CommandId, entry.Output);
            if (_selectedCommand?.Id == entry.CommandId) latestSelectedOutput = entry.Output;
        }

        if (latestSelectedOutput is not null) OutputList.ScrollIntoView(latestSelectedOutput);
        if (_pendingOutput.HasPending)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingOutput));
            return;
        }

        Interlocked.Exchange(ref _outputFlushScheduled, 0);
        if (_pendingOutput.HasPending && Interlocked.Exchange(ref _outputFlushScheduled, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingOutput));
        }
    }

    private void AppendOutput(Guid commandId, CommandOutput output)
    {
        var logs = GetLogs(commandId);
        logs.Add(output);
        while (logs.Count > 10_000) logs.RemoveAt(0);
        if (_selectedCommand?.Id == commandId)
        {
            EmptyOutputHint.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateEmptyStates()
    {
        EmptyProjectsHint.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyCommandsHint.Visibility = _selectedProject is not null && _selectedProject.Commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedCommand is null) EmptyOutputHint.Visibility = Visibility.Visible;
    }

}
