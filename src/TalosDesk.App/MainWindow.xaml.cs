using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
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
    private readonly Dictionary<Guid, ObservableCollection<CommandOutput>> _logs = [];
    private bool _canSave = true;
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
            SaveStatusText.Text = "CONFIG ERROR";
            MessageBox.Show(this,
                $"TalosDesk could not read its local workspace file. The existing file has been left untouched.\n\n{exception.Message}",
                "Workspace could not be loaded", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProjectList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedProject = ProjectList.SelectedItem as ProjectDefinition;
        _selectedCommand = null;
        CommandList.ItemsSource = _selectedProject?.Commands;
        CommandList.SelectedIndex = -1;
        ProjectNameText.Text = _selectedProject?.Name ?? "Choose a project";
        ProjectPathText.Text = _selectedProject?.Directory ?? "Add a local folder to get started";
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
        AddCommandButton.IsEnabled = _selectedProject is not null && !_isStoppingForClose && _canSave;
        ProjectNameText.Text = _selectedProject?.Name ?? "Choose a project";
        ProjectPathText.Text = _selectedProject?.Directory ?? "Add a local folder to get started";
        CommandCountText.Text = _selectedProject?.Commands.Count.ToString() ?? "0";
        SelectedCommandName.Text = command?.Name ?? "Select a command";
        LogCommandName.Text = command is null ? "Select a command to view its output" : $"  {command.Name}";
        RunButton.IsEnabled = command is not null && !running && !_isStoppingForClose && _canSave;
        StopButton.IsEnabled = command is not null && running && !_isStoppingForClose;
        RestartButton.IsEnabled = command is not null && !_isStoppingForClose && _canSave;
        EditButton.IsEnabled = command is not null && !running && !_isStoppingForClose && _canSave;
        CopyButton.IsEnabled = command is not null;
        SaveStatusText.Text = _canSave ? "LOCAL CONFIG" : "CONFIG ERROR";

        if (command is null)
        {
            RunStateText.Text = "IDLE";
            RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            OutputList.ItemsSource = null;
            EmptyOutputHint.Visibility = Visibility.Visible;
            return;
        }

        OutputList.ItemsSource = GetLogs(command.Id);
        EmptyOutputHint.Visibility = GetLogs(command.Id).Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            RunStateText.Text = "RUNNING";
            RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else if (_lastResults.TryGetValue(command.Id, out var result))
        {
            RunStateText.Text = result.State == CommandRunState.Succeeded ? $"SUCCEEDED · EXIT {result.ExitCode}" : result.State == CommandRunState.Stopped ? "STOPPED" : $"FAILED · EXIT {result.ExitCode}";
            RunStateText.Foreground = result.State == CommandRunState.Succeeded ? System.Windows.Media.Brushes.SeaGreen : (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else
        {
            RunStateText.Text = "IDLE";
            RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        }

        if (GetLogs(command.Id).Count > 0) OutputList.ScrollIntoView(GetLogs(command.Id)[^1]);
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        if (!_canSave) return;
        var picker = new OpenFolderDialog { Title = "Choose a local project folder", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;

        var defaultName = Path.GetFileName(Path.TrimEndingDirectorySeparator(picker.FolderName));
        var prompt = new TextPromptWindow("Add project", "Project name", defaultName) { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;

        var project = new ProjectDefinition { Name = prompt.Value.Trim(), Directory = Path.GetFullPath(picker.FolderName) };
        Projects.Add(project);
        ProjectList.SelectedItem = project;
        await SaveWorkspaceAsync();
    }

    private async void ExportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "Export TalosDesk workspace",
            Filter = "TalosDesk workspace (*.talosdesk.json)|*.talosdesk.json|JSON files (*.json)|*.json",
            FileName = "talosdesk-workspace.talosdesk.json",
            AddExtension = true,
            DefaultExt = ".talosdesk.json"
        };
        if (picker.ShowDialog(this) != true) return;

        try
        {
            await WorkspaceStore.WriteFileAsync(picker.FileName, new WorkspaceConfiguration { Projects = Projects.ToList() });
            SaveStatusText.Text = "WORKSPACE EXPORTED";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"TalosDesk could not export this workspace.\n\n{exception.Message}", "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (!_canSave) return;
        if (_sessions.Values.Any(session => !session.Completion.IsCompleted))
        {
            MessageBox.Show(this, "Stop all running commands before importing a workspace. This keeps active commands aligned with their saved configuration.", "Commands are running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = "Import TalosDesk workspace",
            Filter = "TalosDesk workspace (*.talosdesk.json;*.json)|*.talosdesk.json;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        WorkspaceConfiguration imported;
        try
        {
            imported = await WorkspaceStore.ReadFileAsync(picker.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, $"TalosDesk could not read this workspace file. No changes were made.\n\n{exception.Message}", "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var preview = BuildImportPreview(imported);
        if (MessageBox.Show(this, preview, "Review workspace import", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;

        try
        {
            var mergedProjects = Projects.Select(CloneProject).ToList();
            if (!MergeImportedWorkspace(imported, mergedProjects)) return;
            var previousProjects = Projects.ToList();
            Projects.Clear();
            foreach (var project in mergedProjects) Projects.Add(project);
            RefreshProjectList();
            UpdateEmptyStates();
            if (!await SaveWorkspaceAsync())
            {
                Projects.Clear();
                foreach (var project in previousProjects) Projects.Add(project);
                RefreshProjectList();
                UpdateEmptyStates();
                return;
            }
            SaveStatusText.Text = "WORKSPACE IMPORTED";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"TalosDesk could not save the imported workspace.\n\n{exception.Message}", "Import was not saved", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            var omittedCommands = project.Commands.Count > 8 ? $"\n    … {project.Commands.Count - 8} more command(s)" : string.Empty;
            return $"• {project.Name}\n  {project.Directory}\n{string.Join("\n", commands)}{omittedCommands}";
        });
        var omittedProjects = imported.Projects.Count > 12 ? $"\n… {imported.Projects.Count - 12} more project(s)" : string.Empty;

        return $"Preview: {imported.Projects.Count} project(s), {imported.Projects.Sum(project => project.Commands.Count)} command(s).\n" +
               $"New projects: {newProjects} · matching folders: {matchingProjects}\n" +
               $"New commands: {newCommands} · matching names: {projectDetails}\n\n" +
               $"{string.Join("\n\n", visibleProjects)}{omittedProjects}\n\n" +
               "Nothing will run automatically. For each matching project and command, you can choose to keep the local version or replace it with the imported version. Continue?";
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
                $"A project already uses this folder:\n{existingProject.Directory}\n\nLocal name: {existingProject.Name}\nImported name: {incomingProject.Name}\n\nYes: replace project details\nNo: keep local details\nCancel: stop importing",
                "Project already exists", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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
                    $"A command with this name already exists in '{existingProject.Name}'.\n\nName: {existingCommand.Name}\nLocal: {existingCommand.Command}\nImported: {incomingCommand.Command}\n\nYes: replace command\nNo: keep local command\nCancel: stop importing",
                    "Command already exists", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = Projects;
        ProjectList.SelectedItem = Projects.FirstOrDefault(project => project.Id == selectedId);
        if (ProjectList.SelectedItem is null && Projects.Count > 0) ProjectList.SelectedIndex = 0;
    }

    private async void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!_canSave || _selectedProject is null) return;
        var editor = new CommandEditorWindow(_selectedProject.Directory) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        _selectedProject.Commands.Add(editor.Result);
        CommandList.ItemsSource = null;
        CommandList.ItemsSource = _selectedProject.Commands;
        CommandList.SelectedItem = editor.Result;
        UpdateEmptyStates();
        await SaveWorkspaceAsync();
    }

    private async void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!_canSave || _selectedProject is null || _selectedCommand is null) return;
        var editor = new CommandEditorWindow(_selectedProject.Directory, _selectedCommand) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var index = _selectedProject.Commands.IndexOf(_selectedCommand);
        _selectedProject.Commands[index] = editor.Result;
        _logs.Remove(editor.Result.Id);
        _lastResults.Remove(editor.Result.Id);
        CommandList.ItemsSource = null;
        CommandList.ItemsSource = _selectedProject.Commands;
        CommandList.SelectedItem = editor.Result;
        await SaveWorkspaceAsync();
    }

    private void RunCommand_Click(object sender, RoutedEventArgs e) => StartSelectedCommand();

    private async void RestartCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null) return;
        var commandId = _selectedCommand.Id;
        if (_sessions.TryGetValue(commandId, out var session) && !session.Completion.IsCompleted)
        {
            RunStateText.Text = "RESTARTING · STOPPING";
            RunButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            try
            {
                var stopResult = await session.StopAsync();
                await session.Completion;
                if (stopResult == CommandStopResult.AlreadyExited && !session.Completion.IsCompleted) return;
                _sessions.Remove(commandId);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"TalosDesk could not stop this command, so it was not restarted.\n\n{exception.Message}", "Restart stopped", MessageBoxButton.OK, MessageBoxImage.Error);
                RefreshCommandSelection();
                return;
            }
        }

        StartSelectedCommand();
    }

    private void StartSelectedCommand()
    {
        if (_selectedProject is null || _selectedCommand is null || !_canSave) return;
        var command = _selectedCommand;
        var workingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? _selectedProject.Directory : command.WorkingDirectory;
        try
        {
            _lastResults.Remove(command.Id);
            var logs = GetLogs(command.Id);
            logs.Clear();
            var started = _runner.Start(command.Id, command.Command, workingDirectory,
                (_, output) => Dispatcher.BeginInvoke(new Action(() => AppendOutput(command.Id, output))));
            _sessions[command.Id] = started;
            RefreshCommandSelection();
            _ = CompleteRunAsync(command.Id, started);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "Command could not start", MessageBoxButton.OK, MessageBoxImage.Error);
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
                RunStateText.Text = "PROCESS ERROR";
                RunStateText.ToolTip = exception.Message;
            });
        }
    }

    private async void StopCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null || !_sessions.TryGetValue(_selectedCommand.Id, out var session)) return;
        var stoppedCommandId = _selectedCommand.Id;
        RunStateText.Text = "STOPPING · CTRL+C";
        StopButton.IsEnabled = false;
        try
        {
            var stopResult = await session.StopAsync();
            await session.Completion;
            if (_selectedCommand?.Id == stoppedCommandId)
            {
                RunStateText.Text = stopResult == CommandStopResult.ForceTerminated ? "FORCE STOPPED" : "STOPPED";
                StopButton.IsEnabled = false;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"TalosDesk could not confirm that the command stopped.\n\n{exception.Message}", "Stop did not complete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null) return;
        Clipboard.SetText(_selectedCommand.Command);
        SaveStatusText.Text = "COMMAND COPIED";
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null) return;
        GetLogs(_selectedCommand.Id).Clear();
        EmptyOutputHint.Visibility = Visibility.Visible;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        var active = _sessions.Values.Where(session => !session.Completion.IsCompleted).ToArray();
        if (active.Length == 0) return;

        e.Cancel = true;
        var choice = MessageBox.Show(this,
            $"{active.Length} command(s) are still running. Stop them and exit?",
            "Commands are running", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
            MessageBox.Show(this, $"TalosDesk could not confirm that every command stopped, so it stayed open.\n\n{exception.Message}", "Commands are still running", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> SaveWorkspaceAsync()
    {
        if (!_canSave) return false;
        SaveStatusText.Text = "SAVING…";
        try
        {
            await _store.SaveAsync(new WorkspaceConfiguration { Projects = Projects.ToList() });
            SaveStatusText.Text = "SAVED LOCALLY";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveStatusText.Text = "SAVE FAILED";
            MessageBox.Show(this, $"TalosDesk could not save your project configuration.\n\n{exception.Message}", "Configuration was not saved", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private ObservableCollection<CommandOutput> GetLogs(Guid commandId)
    {
        if (!_logs.TryGetValue(commandId, out var logs)) _logs[commandId] = logs = [];
        return logs;
    }

    private void AppendOutput(Guid commandId, CommandOutput output)
    {
        var logs = GetLogs(commandId);
        logs.Add(output);
        while (logs.Count > 10_000) logs.RemoveAt(0);
        if (_selectedCommand?.Id == commandId)
        {
            EmptyOutputHint.Visibility = Visibility.Collapsed;
            OutputList.ScrollIntoView(output);
        }
    }

    private void UpdateEmptyStates()
    {
        EmptyProjectsHint.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyCommandsHint.Visibility = _selectedProject is not null && _selectedProject.Commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedCommand is null) EmptyOutputHint.Visibility = Visibility.Visible;
    }

}
