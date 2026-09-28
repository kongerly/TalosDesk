using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.App;

public partial class MainWindow : Window
{
    private const double CommandDragEdgeSize = 44;
    private const double CommandDragScrollStep = 18;

    private enum MainPage { Overview, Commands, Groups, Output }

    private readonly WorkspaceStore _store;
    private readonly CommandRunner _runner = new();
    private readonly RunLogStore _runLogStore;
    private bool _logStoreAvailable;
    private string? _logInitError;
    private readonly Dictionary<Guid, RunLogWriter> _runLogWriters = [];
    private readonly Dictionary<Guid, Task> _logFinalizations = [];
    private readonly Dictionary<Guid, string> _logWarnings = [];
    private readonly ObservableCollection<RunHistoryItem> _runHistory = [];
    private bool _syncingRunHistory;
    private readonly Dictionary<Guid, CommandRunSession> _sessions = [];
    private readonly Dictionary<Guid, CommandRunResult> _lastResults = [];
    private readonly Dictionary<Guid, DateTimeOffset> _runStartedAt = [];
    private readonly Dictionary<Guid, long> _runVersions = [];
    private readonly HashSet<Guid> _restartsInProgress = [];
    private readonly HashSet<Guid> _checkedCommandIds = [];
    private readonly HashSet<Guid> _reservedCommandIds = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _sequentialGroupCancellations = [];
    private readonly Dictionary<Guid, Task> _sequentialGroupTasks = [];
    private readonly Dictionary<Guid, Dictionary<Guid, CommandRunSession>> _parallelGroupSessions = [];
    private readonly Dictionary<Guid, long> _parallelGroupVersions = [];
    private readonly Dictionary<Guid, string> _groupStatuses = [];
    private readonly Dictionary<Guid, ObservableCollection<CommandOutput>> _logs = [];
    private readonly ObservableCollection<OutputCommandItem> _outputCommands = [];
    private readonly BoundedOutputInbox _pendingOutput = new(10_000);
    private int _outputFlushScheduled;
    private bool _canSave = true;
    private bool _saveFailed;
    private bool _workspaceChangeInProgress;
    private bool _allowClose;
    private bool _isStoppingForClose;
    private ProjectDefinition? _selectedProject;
    private CommandDefinition? _selectedCommand;
    private Guid? _batchProjectId;
    private bool _syncingCommandSelection;
    private Guid? _renderedOutputCommandId;
    private ObservableCollection<CommandOutput>? _renderedOutputLogs;
    private bool _syncingOutputText;
    private bool _outputViewNeedsResync;
    private bool _outputFollowEnabled = true;
    private readonly DispatcherTimer _commandDragScrollTimer;
    private CommandDefinition? _pendingCommandDrag;
    private CommandDefinition? _activeCommandDrag;
    private ProjectDefinition? _commandDragProject;
    private List<CommandDefinition>? _commandDragOriginalOrder;
    private ListBoxItem? _commandDragVisualItem;
    private Point _commandDragStartPoint;
    private int _commandDragScrollDirection;
    private bool _commandDragActive;
    private bool _commandDragDropped;
    private bool _commandDragCancelRequested;
    private bool _commandDragWorkspaceChangeActive;
    private WorkspaceRevision? _workspaceRevision;
    private readonly string _profileLabel;
    private bool _workspaceLoaded;
    private bool _checkingWorkspaceRevision;
    private bool _externalWorkspaceChangeReported;

    public MainWindow()
        : this(new WorkspaceStore(), null)
    {
    }

    internal MainWindow(WorkspaceStore store, string? profileLabel)
    {
        _store = store;
        _runLogStore = new RunLogStore(store.FilePath);
        _profileLabel = string.IsNullOrWhiteSpace(profileLabel) ? "正式工作区" : profileLabel;
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(profileLabel))
        {
            Title = $"TalosDesk · {profileLabel}";
        }
        WorkspaceProfileText.Text = _profileLabel;
        WorkspacePathText.Text = _store.FilePath;
        WorkspacePathText.ToolTip = _store.FilePath;
        _commandDragScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _commandDragScrollTimer.Tick += CommandDragScrollTimer_Tick;
        OutputCommandList.ItemsSource = _outputCommands;
        RunHistoryComboBox.ItemsSource = _runHistory;
        HistoryStreamComboBox.SelectedIndex = 0;
        UpdateLogPathDisplay();
        GroupList.ItemsSource = GroupItems;
        DataContext = this;
        SetPage(MainPage.Overview);
    }

    public ObservableCollection<ProjectDefinition> Projects { get; } = [];
    public ObservableCollection<CommandGroupItem> GroupItems { get; } = [];

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            try { _runLogStore.Initialize(); _logStoreAvailable = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
            {
                _logInitError = $"日志初始化失败：{exception.Message}";
                LogStatusText.Text = _logInitError;
            }
            UpdateLogPathDisplay();
            LogLimitMbTextBox.Text = (_runLogStore.Settings.MaxBytes / (1024 * 1024)).ToString();
            LogRetentionDaysTextBox.Text = _runLogStore.Settings.RetentionDays.ToString();
            var snapshot = await _store.LoadSnapshotAsync();
            var configuration = snapshot.Configuration;
            _workspaceRevision = snapshot.Revision;
            foreach (var project in configuration.Projects)
            {
                project.Commands ??= [];
                project.Groups ??= [];
                Projects.Add(project);
            }

            if (Projects.Count > 0) ProjectList.SelectedIndex = 0;
            UpdateEmptyStates();
            _workspaceLoaded = true;
            UpdateWorkspaceIdentity();
            _ = RefreshWorkspaceAfterStartupAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            _canSave = false;
            RefreshCommandSelection();
            UpdateEmptyStates();
            MessageBox.Show(this,
                $"TalosDesk 无法读取本机工作区配置，原文件未作修改。\n\n{exception.Message}",
                "无法加载工作区", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshWorkspaceAfterStartupAsync()
    {
        await Task.Delay(250);
        if (IsLoaded) await ReloadWorkspaceForSecondaryLaunchAsync();
    }

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (!_workspaceLoaded || !_canSave || _workspaceChangeInProgress || _checkingWorkspaceRevision) return;
        _checkingWorkspaceRevision = true;
        try
        {
            await EnsureWorkspaceUnchangedAsync(showDialog: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveStatusText.Text = "暂时无法检查配置";
        }
        finally
        {
            _checkingWorkspaceRevision = false;
        }
    }

    internal async Task ReloadWorkspaceForSecondaryLaunchAsync()
    {
        if (!_workspaceLoaded || _workspaceChangeInProgress || _restartsInProgress.Count > 0 ||
            _sequentialGroupCancellations.Count > 0 || _sessions.Values.Any(session => !session.Completion.IsCompleted))
        {
            SaveStatusText.Text = "已有任务运行，未重新加载配置";
            return;
        }

        _workspaceChangeInProgress = true;
        RefreshCommandSelection();
        try
        {
            var snapshot = await _store.LoadSnapshotAsync();
            var configuration = snapshot.Configuration;
            foreach (var project in configuration.Projects)
            {
                project.Commands ??= [];
                project.Groups ??= [];
            }

            ReplaceProjects(configuration.Projects);
            _workspaceRevision = snapshot.Revision;
            _canSave = true;
            _saveFailed = false;
            _externalWorkspaceChangeReported = false;
            UpdateWorkspaceIdentity();
            SaveStatusText.Text = "已从磁盘重新加载";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            SaveStatusText.Text = "重新加载失败";
            MessageBox.Show(this,
                $"TalosDesk 无法重新加载工作区，当前窗口内容保持不变。\n\n{exception.Message}",
                "无法重新加载工作区", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _workspaceChangeInProgress = false;
            RefreshCommandSelection();
        }
    }

    private void UpdateWorkspaceIdentity()
    {
        WorkspaceProfileText.Text = $"{_profileLabel} · {Projects.Count} 个项目";
    }

    private void ProjectList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedProject = ProjectList.SelectedItem as ProjectDefinition;
        if (_selectedProject is not null && _batchProjectId != _selectedProject.Id)
        {
            _checkedCommandIds.Clear();
            _batchProjectId = _selectedProject.Id;
        }
        _selectedCommand = null;
        RefreshRunHistory(selectCurrent: true);
        _syncingCommandSelection = true;
        try
        {
            CommandList.ItemsSource = _selectedProject?.Commands;
            CommandList.SelectedItem = null;
            RebuildOutputCommands();
            RebuildGroupItems();
            OutputCommandList.SelectedItem = null;
        }
        finally { _syncingCommandSelection = false; }
        RefreshCommandSelection();
        UpdateEmptyStates();
    }

    private void CommandList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_syncingCommandSelection) SelectCommand(CommandList.SelectedItem as CommandDefinition);
    }

    private void CommandList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPendingCommandDrag();
        if (e.ClickCount > 1 || e.OriginalSource is not DependencyObject source) return;
        var item = FindVisualAncestor<ListBoxItem>(source);
        if (item?.DataContext is not CommandDefinition command ||
            !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), CommandList) ||
            IsInteractiveCommandChild(source, item) || !CanReorderCommands()) return;

        _pendingCommandDrag = command;
        _commandDragStartPoint = e.GetPosition(CommandList);
    }

    private void CommandList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_commandDragActive) CancelPendingCommandDrag();
    }

    private async void CommandList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_commandDragActive || _pendingCommandDrag is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPendingCommandDrag();
            return;
        }

        var position = e.GetPosition(CommandList);
        if (Math.Abs(position.X - _commandDragStartPoint.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _commandDragStartPoint.Y) <= SystemParameters.MinimumVerticalDragDistance) return;

        var command = _pendingCommandDrag;
        _pendingCommandDrag = null;
        await BeginCommandDragAsync(command);
    }

    private async void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CancelPendingCommandDrag();
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        var item = FindVisualAncestor<ListBoxItem>(source);
        if (item?.DataContext is not CommandDefinition command ||
            !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), CommandList) ||
            IsInteractiveCommandChild(source, item)) return;

        SelectCommand(command);
        if (!CanEditCommand(command)) return;
        e.Handled = true;
        await EditSelectedCommandAsync();
    }

    private async Task BeginCommandDragAsync(CommandDefinition command)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed ||
            _selectedProject?.Commands.Contains(command) != true || !CanReorderCommands()) return;

        SelectCommand(command);
        if (!BeginWorkspaceChange()) return;

        _commandDragWorkspaceChangeActive = true;
        _commandDragProject = _selectedProject;
        _commandDragOriginalOrder = _selectedProject!.Commands.ToList();
        _activeCommandDrag = command;
        _commandDragActive = true;
        _commandDragDropped = false;
        _commandDragCancelRequested = false;
        BatchSelectionHint.Text = "拖动中 · 松开鼠标保存，Esc 取消";
        UpdateCommandDragVisual();

        try
        {
            var data = new DataObject(typeof(CommandDefinition), command);
            var effect = DragDrop.DoDragDrop(CommandList, data, DragDropEffects.Move);
            await CompleteCommandDragAsync(effect == DragDropEffects.Move && _commandDragDropped && !_commandDragCancelRequested);
        }
        catch (Exception exception)
        {
            await CompleteCommandDragAsync(commit: false);
            MessageBox.Show(this, $"TalosDesk 无法完成命令拖动。\n\n{exception.Message}",
                "命令排序失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CommandList_DragOver(object sender, DragEventArgs e)
    {
        if (!TryGetActiveDraggedCommand(e.Data, out _))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var position = e.GetPosition(CommandList);
        UpdateCommandDragAutoScroll(position);
        ReorderDraggedCommandAt(position);
    }

    private void CommandList_DragLeave(object sender, DragEventArgs e) => StopCommandDragAutoScroll();

    private void CommandList_Drop(object sender, DragEventArgs e)
    {
        StopCommandDragAutoScroll();
        if (!TryGetActiveDraggedCommand(e.Data, out _))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        ReorderDraggedCommandAt(e.GetPosition(CommandList));
        _commandDragDropped = true;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void CommandList_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (!_commandDragActive) return;
        if (e.EscapePressed || _commandDragCancelRequested)
        {
            _commandDragCancelRequested = true;
            e.Action = DragAction.Cancel;
            e.Handled = true;
        }
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        CancelPendingCommandDrag();
        if (_commandDragActive) _commandDragCancelRequested = true;
    }

    private void CommandDragScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_commandDragActive || _commandDragScrollDirection == 0)
        {
            StopCommandDragAutoScroll();
            return;
        }

        var scrollViewer = FindVisualDescendant<ScrollViewer>(CommandList);
        if (scrollViewer is null) return;
        var destination = Math.Clamp(
            scrollViewer.VerticalOffset + _commandDragScrollDirection * CommandDragScrollStep,
            0,
            scrollViewer.ScrollableHeight);
        if (Math.Abs(destination - scrollViewer.VerticalOffset) < 0.1) return;
        scrollViewer.ScrollToVerticalOffset(destination);
        CommandList.UpdateLayout();
        ReorderDraggedCommandAt(Mouse.GetPosition(CommandList));
    }

    private void UpdateCommandDragAutoScroll(Point position)
    {
        var direction = position.Y <= CommandDragEdgeSize
            ? -1
            : position.Y >= CommandList.ActualHeight - CommandDragEdgeSize
                ? 1
                : 0;
        _commandDragScrollDirection = direction;
        if (direction == 0) _commandDragScrollTimer.Stop();
        else if (!_commandDragScrollTimer.IsEnabled) _commandDragScrollTimer.Start();
    }

    private void StopCommandDragAutoScroll()
    {
        _commandDragScrollDirection = 0;
        _commandDragScrollTimer.Stop();
    }

    private void ReorderDraggedCommandAt(Point position)
    {
        var project = _commandDragProject;
        var command = _activeCommandDrag;
        if (!_commandDragActive || project is null || command is null || !ReferenceEquals(project, _selectedProject)) return;

        var hit = CommandList.InputHitTest(position) as DependencyObject;
        var targetItem = FindVisualAncestor<ListBoxItem>(hit);
        if (targetItem?.DataContext is not CommandDefinition target ||
            !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(targetItem), CommandList) ||
            target.Id == command.Id) return;

        var sourceIndex = project.Commands.IndexOf(command);
        var targetIndex = project.Commands.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return;
        var pointInTarget = CommandList.TranslatePoint(position, targetItem);
        if (sourceIndex < targetIndex && pointInTarget.Y < targetItem.ActualHeight / 2) return;
        if (sourceIndex > targetIndex && pointInTarget.Y >= targetItem.ActualHeight / 2) return;

        project.Commands.RemoveAt(sourceIndex);
        project.Commands.Insert(targetIndex, command);
        CommandList.Items.Refresh();
        _syncingCommandSelection = true;
        try { CommandList.SelectedItem = command; }
        finally { _syncingCommandSelection = false; }
        UpdateCommandDragVisual();
    }

    private async Task CompleteCommandDragAsync(bool commit)
    {
        if (!_commandDragWorkspaceChangeActive) return;
        var project = _commandDragProject;
        var command = _activeCommandDrag;
        var originalOrder = _commandDragOriginalOrder;
        StopCommandDragAutoScroll();
        ClearCommandDragVisual();

        try
        {
            var changed = project is not null && originalOrder is not null &&
                !project.Commands.Select(item => item.Id).SequenceEqual(originalOrder.Select(item => item.Id));
            if (!commit && changed && project is not null && originalOrder is not null)
            {
                RestoreCommandOrder(project, originalOrder);
            }
            else if (commit && changed && project is not null && originalOrder is not null && !await SaveWorkspaceAsync())
            {
                RestoreCommandOrder(project, originalOrder);
            }

            RefreshCommandList(command is not null && project?.Commands.Contains(command) == true ? command : null);
        }
        finally
        {
            _commandDragActive = false;
            _commandDragDropped = false;
            _commandDragCancelRequested = false;
            _commandDragWorkspaceChangeActive = false;
            _activeCommandDrag = null;
            _commandDragProject = null;
            _commandDragOriginalOrder = null;
            EndWorkspaceChange();
        }
    }

    private static void RestoreCommandOrder(ProjectDefinition project, IEnumerable<CommandDefinition> originalOrder)
    {
        project.Commands.Clear();
        project.Commands.AddRange(originalOrder);
    }

    private void CancelPendingCommandDrag()
    {
        _pendingCommandDrag = null;
    }

    private bool TryGetActiveDraggedCommand(IDataObject data, out CommandDefinition? command)
    {
        command = data.GetDataPresent(typeof(CommandDefinition))
            ? data.GetData(typeof(CommandDefinition)) as CommandDefinition
            : null;
        return _commandDragActive && command is not null && ReferenceEquals(command, _activeCommandDrag);
    }

    private bool CanReorderCommands() => _selectedProject is not null && _canSave && !_workspaceChangeInProgress &&
        _restartsInProgress.Count == 0 && _sequentialGroupCancellations.Count == 0 && !_isStoppingForClose;

    private bool CanEditCommand(CommandDefinition command) => _selectedProject?.Commands.Contains(command) == true &&
        !(_sessions.TryGetValue(command.Id, out var session) && !session.Completion.IsCompleted) && CanOpenWorkspaceEditor();

    private void UpdateCommandDragVisual()
    {
        if (_commandDragVisualItem is not null)
        {
            _commandDragVisualItem.ClearValue(OpacityProperty);
            _commandDragVisualItem.ClearValue(CursorProperty);
        }

        CommandList.Cursor = Cursors.SizeAll;
        _commandDragVisualItem = _activeCommandDrag is null
            ? null
            : CommandList.ItemContainerGenerator.ContainerFromItem(_activeCommandDrag) as ListBoxItem;
        if (_commandDragVisualItem is not null)
        {
            _commandDragVisualItem.Opacity = 0.68;
            _commandDragVisualItem.Cursor = Cursors.SizeAll;
        }
    }

    private void ClearCommandDragVisual()
    {
        if (_commandDragVisualItem is not null)
        {
            _commandDragVisualItem.ClearValue(OpacityProperty);
            _commandDragVisualItem.ClearValue(CursorProperty);
            _commandDragVisualItem = null;
        }
        CommandList.ClearValue(CursorProperty);
    }

    private static bool IsInteractiveCommandChild(DependencyObject source, ListBoxItem item)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, item); current = GetVisualParent(current))
        {
            if (current is ButtonBase) return true;
        }
        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetVisualParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static DependencyObject? GetVisualParent(DependencyObject source) => source switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(source)
    };

    private void OutputCommandList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_syncingCommandSelection) SelectCommand((OutputCommandList.SelectedItem as OutputCommandItem)?.Command);
    }

    private void SelectCommand(CommandDefinition? command)
    {
        var previousId = _selectedCommand?.Id;
        _selectedCommand = command is not null && _selectedProject?.Commands.Contains(command) == true ? command : null;
        _syncingCommandSelection = true;
        try
        {
            if (!ReferenceEquals(CommandList.SelectedItem, _selectedCommand)) CommandList.SelectedItem = _selectedCommand;
            var outputItem = _outputCommands.FirstOrDefault(item => item.Command.Id == _selectedCommand?.Id);
            if (!ReferenceEquals(OutputCommandList.SelectedItem, outputItem)) OutputCommandList.SelectedItem = outputItem;
        }
        finally { _syncingCommandSelection = false; }
        if (previousId != _selectedCommand?.Id) RefreshRunHistory(selectCurrent: false);
        RefreshCommandSelection();
    }

    private void RebuildOutputCommands()
    {
        _outputCommands.Clear();
        if (_selectedProject is null) return;
        foreach (var command in _selectedProject.Commands) _outputCommands.Add(new OutputCommandItem(command));
    }

    private void RebuildGroupItems()
    {
        GroupItems.Clear();
        if (_selectedProject is null)
        {
            EmptyGroupsHint.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var group in _selectedProject.Groups) GroupItems.Add(new CommandGroupItem(group));
        RefreshGroupItems();
        EmptyGroupsHint.Visibility = _selectedProject.Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OverviewPage_Click(object sender, RoutedEventArgs e) => SetPage(MainPage.Overview);
    private void CommandsPage_Click(object sender, RoutedEventArgs e) => SetPage(MainPage.Commands);
    private void GroupsPage_Click(object sender, RoutedEventArgs e) => SetPage(MainPage.Groups);
    private void OutputPage_Click(object sender, RoutedEventArgs e) => SetPage(MainPage.Output);

    private void SetPage(MainPage page)
    {
        OverviewPage.Visibility = page == MainPage.Overview ? Visibility.Visible : Visibility.Collapsed;
        CommandsPage.Visibility = page == MainPage.Commands ? Visibility.Visible : Visibility.Collapsed;
        GroupsPage.Visibility = page == MainPage.Groups ? Visibility.Visible : Visibility.Collapsed;
        OutputPage.Visibility = page == MainPage.Output ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, buttonPage) in new[]
                 {
                     (OverviewPageButton, MainPage.Overview),
                     (CommandsPageButton, MainPage.Commands),
                     (GroupsPageButton, MainPage.Groups),
                     (OutputPageButton, MainPage.Output)
                 })
        {
            button.Background = buttonPage == page
                ? (System.Windows.Media.Brush)FindResource("HeroBrush")
                : (System.Windows.Media.Brush)FindResource("SidebarRaisedBrush");
            button.BorderBrush = buttonPage == page
                ? (System.Windows.Media.Brush)FindResource("GoldBrush")
                : (System.Windows.Media.Brush)FindResource("SidebarRaisedBrush");
        }
    }

    private void BatchCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CommandDefinition command } checkBox)
        {
            checkBox.IsChecked = _selectedProject?.Commands.Contains(command) == true && _checkedCommandIds.Contains(command.Id);
        }
    }

    private void BatchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: CommandDefinition command } checkBox ||
            _selectedProject?.Commands.Contains(command) != true) return;
        if (checkBox.IsChecked == true) _checkedCommandIds.Add(command.Id);
        else _checkedCommandIds.Remove(command.Id);
        RefreshCommandSelection();
    }

    private void RefreshCommandSelection()
    {
        var command = _selectedCommand;
        var running = command is not null && _sessions.TryGetValue(command.Id, out var session) && !session.Completion.IsCompleted;
        var restarting = command is not null && _restartsInProgress.Contains(command.Id);
        var reserved = command is not null && _reservedCommandIds.Contains(command.Id);
        var canUseWorkspace = !_workspaceChangeInProgress && _restartsInProgress.Count == 0 && !_isStoppingForClose && _canSave;
        var canChangeWorkspace = canUseWorkspace && _sequentialGroupCancellations.Count == 0;
        AddProjectButton.IsEnabled = canChangeWorkspace;
        ImportButton.IsEnabled = canChangeWorkspace;
        ExportButton.IsEnabled = canUseWorkspace;
        AddCommandButton.IsEnabled = _selectedProject is not null && canChangeWorkspace;
        AddGroupButton.IsEnabled = _selectedProject?.Commands.Count > 0 && canChangeWorkspace;
        EditProjectButton.IsEnabled = _selectedProject is not null && canChangeWorkspace;
        OverviewCommandsButton.IsEnabled = _selectedProject is not null;
        OverviewOutputButton.IsEnabled = _selectedProject is not null;
        ProjectNameText.Text = _selectedProject?.Name ?? "请选择项目";
        ProjectPathText.Text = _selectedProject?.Directory ?? "添加本地文件夹以开始使用";
        CommandsProjectText.Text = _selectedProject?.Name ?? "选择项目以查看命令";
        GroupsProjectText.Text = _selectedProject?.Name ?? "选择项目以查看分组";
        OutputProjectText.Text = _selectedProject?.Name ?? "选择项目以查看输出";
        CommandCountText.Text = _selectedProject?.Commands.Count.ToString() ?? "0";
        OverviewRunningCountText.Text = (_selectedProject?.Commands.Count(item =>
            _sessions.TryGetValue(item.Id, out var active) && !active.Completion.IsCompleted) ?? 0).ToString();
        OverviewFinishedCountText.Text = (_selectedProject?.Commands.Count(item => _lastResults.ContainsKey(item.Id)) ?? 0).ToString();
        foreach (var item in _outputCommands) item.StatusText = GetCommandStatusText(item.Command);
        SelectedCommandName.Text = command?.Name ?? "请选择命令";
        RunStartedText.Text = SelectedHistoricalRun is { } selectedRun
            ? $"开始时间：{selectedRun.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : command is not null && _runStartedAt.TryGetValue(command.Id, out var startedAt)
            ? $"开始时间：{startedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : string.Empty;
        LogCommandName.Text = command?.Name ?? "选择命令以查看输出";
        RunButton.IsEnabled = command is not null && !running && !restarting && !reserved && !_workspaceChangeInProgress && !_isStoppingForClose && _canSave;
        StopButton.IsEnabled = command is not null && running && !restarting && !_isStoppingForClose;
        RestartButton.IsEnabled = command is not null && !restarting && !reserved && !_workspaceChangeInProgress && !_isStoppingForClose && _canSave;
        OutputRunButton.IsEnabled = RunButton.IsEnabled;
        OutputStopButton.IsEnabled = StopButton.IsEnabled;
        OutputRestartButton.IsEnabled = RestartButton.IsEnabled;
        CopySelectedOutputButton.IsEnabled = command is not null && OutputTextBox.SelectionLength > 0;
        ClearOutputButton.IsEnabled = command is not null && SelectedHistoricalRun is null;
        LogStatusText.Text = SelectedHistoricalRun is { } historicalRun
            ? historicalRun.Truncated ? "该批次日志已达到容量上限，后续输出未保存。" :
                historicalRun.WriteFailed ? "该批次日志写入失败，内容不完整。" : string.Empty
            : command is not null && _logWarnings.TryGetValue(command.Id, out var logWarning) ? logWarning : _logInitError ?? string.Empty;
        EditButton.IsEnabled = command is not null && !running && canChangeWorkspace;
        CopyButton.IsEnabled = command is not null;
        var selectedIndex = command is null ? -1 : _selectedProject?.Commands.IndexOf(command) ?? -1;
        MoveCommandUpButton.IsEnabled = selectedIndex > 0 && canChangeWorkspace;
        MoveCommandDownButton.IsEnabled = _selectedProject is not null && selectedIndex >= 0 && selectedIndex < _selectedProject.Commands.Count - 1 && canChangeWorkspace;
        DeleteCommandButton.IsEnabled = command is not null && !running && !restarting && canChangeWorkspace;
        var checkedCount = _selectedProject?.Commands.Count(item => _checkedCommandIds.Contains(item.Id)) ?? 0;
        var readyCount = _selectedProject?.Commands.Count(item => _checkedCommandIds.Contains(item.Id) &&
            !IsCommandBusy(item.Id)) ?? 0;
        BatchSelectionHint.Text = checkedCount == 0
            ? "勾选同时运行 · 双击编辑 · 拖动排序"
            : $"已勾选 {checkedCount} 条 · 可启动 {readyCount} 条 · 拖动排序";
        BatchRunButton.Content = $"同时运行 ({readyCount})";
        BatchRunButton.IsEnabled = readyCount > 0 && !_workspaceChangeInProgress && !_isStoppingForClose && _canSave;
        SaveStatusText.Text = !_canSave ? "配置错误" : _workspaceChangeInProgress ? "正在处理…" : _saveFailed ? "保存失败" : "本机配置";
        RefreshGroupItems(canChangeWorkspace);

        if (command is null)
        {
            EmptyOutputHint.Text = "选择左侧命令以查看输出。";
            RunStateText.Text = "空闲";
            RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            OutputRunStateText.Text = RunStateText.Text;
            OutputRunStateText.Foreground = RunStateText.Foreground;
            if (_renderedOutputCommandId is not null) RenderSelectedOutput(null);
            EmptyOutputHint.Visibility = Visibility.Visible;
            return;
        }

        var commandLogs = GetLogs(command.Id);
        if (SelectedHistoricalRun is null && (_renderedOutputCommandId != command.Id || !ReferenceEquals(_renderedOutputLogs, commandLogs)))
        {
            RenderSelectedOutput(command, scrollToEnd: true);
        }
        if (SelectedHistoricalRun is null)
        {
            EmptyOutputHint.Text = "这条命令尚无输出。";
            EmptyOutputHint.Visibility = commandLogs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        RunStateText.Text = GetCommandStatusText(command);
        if (running || restarting) RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        else if (_lastResults.TryGetValue(command.Id, out var result))
            RunStateText.Foreground = result.State == CommandRunState.Succeeded
                ? System.Windows.Media.Brushes.SeaGreen
                : (System.Windows.Media.Brush)FindResource("AccentBrush");
        else RunStateText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        OutputRunStateText.Text = SelectedHistoricalRun is { } historicalState ? GetRunStateLabel(historicalState.State) : RunStateText.Text;
        OutputRunStateText.Foreground = RunStateText.Foreground;
    }

    private string GetCommandStatusText(CommandDefinition command)
    {
        if (_restartsInProgress.Contains(command.Id)) return "正在重启";
        if (_sessions.TryGetValue(command.Id, out var session) && !session.Completion.IsCompleted) return "运行中";
        if (_reservedCommandIds.Contains(command.Id)) return "等待分组执行";
        if (!_lastResults.TryGetValue(command.Id, out var result)) return "空闲";
        return result.State == CommandRunState.Succeeded
            ? $"已成功 · 退出码 {result.ExitCode}"
            : result.State == CommandRunState.Stopped
                ? $"{(result.WasForceTerminated ? "已强制停止" : "已停止")} · 退出码 {result.ExitCode}"
                : $"失败 · 退出码 {result.ExitCode}";
    }

    private bool IsCommandBusy(Guid commandId) =>
        _reservedCommandIds.Contains(commandId) ||
        _restartsInProgress.Contains(commandId) ||
        _sessions.TryGetValue(commandId, out var session) && !session.Completion.IsCompleted;

    private void RefreshGroupItems(bool? canEditOverride = null)
    {
        var canEdit = canEditOverride ?? (!_workspaceChangeInProgress && _restartsInProgress.Count == 0 &&
            _sequentialGroupCancellations.Count == 0 && !_isStoppingForClose && _canSave);
        var project = _selectedProject;
        GroupCountText.Text = $"{project?.Groups.Count ?? 0} 个分组";
        if (project is null) return;

        foreach (var item in GroupItems)
        {
            var commands = item.Group.CommandIds
                .Select(id => project.Commands.FirstOrDefault(command => command.Id == id))
                .Where(command => command is not null)
                .Cast<CommandDefinition>()
                .ToArray();
            var readyCount = commands.Count(command => !IsCommandBusy(command.Id));
            var runningCount = commands.Count(command =>
                _sessions.TryGetValue(command.Id, out var session) && !session.Completion.IsCompleted);
            var active = _sequentialGroupCancellations.ContainsKey(item.Group.Id);
            var canRun = item.Group.ExecutionMode == CommandGroupExecutionMode.Parallel
                ? readyCount > 0
                : commands.Length > 0 && !active && commands.All(command => !IsCommandBusy(command.Id));
            var status = _groupStatuses.GetValueOrDefault(item.Group.Id);
            if (item.Group.ExecutionMode == CommandGroupExecutionMode.Parallel && string.IsNullOrWhiteSpace(status) && runningCount > 0)
            {
                status = readyCount > 0
                    ? $"运行中 {runningCount} / {commands.Length} 条 · 另有 {readyCount} 条可启动"
                    : $"运行中 {runningCount} / {commands.Length} 条";
            }
            if (string.IsNullOrWhiteSpace(status))
            {
                status = commands.Length == 0
                    ? "没有成员 · 编辑分组后才能运行"
                    : item.Group.ExecutionMode == CommandGroupExecutionMode.Parallel
                        ? $"可启动 {readyCount} / {commands.Length} 条"
                        : "就绪 · 将依次等待每条任务成功";
            }
            item.Update(commands, status, canRun && !_workspaceChangeInProgress && !_isStoppingForClose && _canSave, canEdit);
        }
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenWorkspaceEditor()) return;
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
        if (!CanOpenWorkspaceEditor() || _selectedProject is null) return;
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
        if (_workspaceChangeInProgress || _restartsInProgress.Count > 0 || _isStoppingForClose || !_canSave) return;
        var confirmation = MessageBox.Show(this,
            "导出文件会以明文包含项目路径和完整命令；如果命令中写有密钥或其他敏感值，它们也会一并导出。\n\n请在分享文件前检查内容。是否继续导出？",
            "确认导出明文工作区", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        var picker = new SaveFileDialog
        {
            Title = "导出 TalosDesk 工作区",
            Filter = "TalosDesk 工作区 (*.talosdesk.json)|*.talosdesk.json|JSON 文件 (*.json)|*.json",
            FileName = "talosdesk-workspace.talosdesk.json",
            AddExtension = true,
            DefaultExt = ".talosdesk.json"
        };
        if (picker.ShowDialog(this) != true) return;

        var tracksWorkspaceChange = _sequentialGroupCancellations.Count == 0;
        if (tracksWorkspaceChange && !BeginWorkspaceChange()) return;
        try
        {
            await WorkspaceStore.WriteFileAsync(picker.FileName, new WorkspaceConfiguration { Projects = Projects.ToList() });
            SaveStatusText.Text = "工作区已导出";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"TalosDesk 无法导出此工作区。\n\n{exception.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (tracksWorkspaceChange) EndWorkspaceChange();
        }
    }

    private async void ImportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceChangeInProgress || _restartsInProgress.Count > 0 || _isStoppingForClose || !_canSave) return;
        if (_restartsInProgress.Count > 0 || _sequentialGroupCancellations.Count > 0 || _sessions.Values.Any(session => !session.Completion.IsCompleted))
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
            var groups = project.Groups.Take(5).Select(group =>
            {
                var memberNames = group.CommandIds.Select(id => project.Commands.First(command => command.Id == id).Name);
                var mode = group.ExecutionMode == CommandGroupExecutionMode.Sequential ? "顺序" : "同时";
                return $"    ◇ {group.Name}（{mode}）：{string.Join(" → ", memberNames)}";
            });
            var groupSection = project.Groups.Count == 0 ? string.Empty : $"\n  分组：\n{string.Join("\n", groups)}";
            var omittedGroups = project.Groups.Count > 5 ? $"\n    … 另有 {project.Groups.Count - 5} 个分组" : string.Empty;
            return $"• {project.Name}\n  {project.Directory}\n{string.Join("\n", commands)}{omittedCommands}{groupSection}{omittedGroups}";
        });
        var omittedProjects = imported.Projects.Count > 12 ? $"\n… 另有 {imported.Projects.Count - 12} 个项目" : string.Empty;

        return $"即将导入 {imported.Projects.Count} 个项目、{imported.Projects.Sum(project => project.Commands.Count)} 条命令、{imported.Projects.Sum(project => project.Groups.Count)} 个分组。\n" +
               $"新增项目：{newProjects} · 文件夹相同：{matchingProjects}\n" +
               $"新增命令：{newCommands} · 名称冲突：{projectDetails}\n\n" +
               $"{string.Join("\n\n", visibleProjects)}{omittedProjects}\n\n" +
               "导入不会自动运行任何命令。遇到同一项目、同名命令或同名分组时，你可以选择保留本机版本或替换为导入版本。是否继续？";
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
                var commandIdMap = new Dictionary<Guid, Guid>();
                for (var index = 0; index < addedProject.Commands.Count; index++)
                {
                    var command = addedProject.Commands[index];
                    var incomingId = incomingProject.Commands[index].Id;
                    if (targetProjects.SelectMany(project => project.Commands).Any(existing => existing.Id == command.Id)) command.Id = Guid.NewGuid();
                    commandIdMap[incomingId] = command.Id;
                }
                foreach (var group in addedProject.Groups)
                {
                    if (targetProjects.SelectMany(project => project.Groups).Any(existing => existing.Id == group.Id)) group.Id = Guid.NewGuid();
                    group.CommandIds = CommandGroupOperations.RemapCommandIds(group.CommandIds, commandIdMap);
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

            var incomingToTargetCommandIds = new Dictionary<Guid, Guid>();
            foreach (var incomingCommand in incomingProject.Commands)
            {
                var existingCommandIndex = existingProject.Commands.FindIndex(command => SameCommandName(command.Name, incomingCommand.Name));
                if (existingCommandIndex < 0)
                {
                    var addedCommand = CloneCommand(incomingCommand);
                    if (targetProjects.SelectMany(project => project.Commands).Any(existing => existing.Id == addedCommand.Id)) addedCommand.Id = Guid.NewGuid();
                    existingProject.Commands.Add(addedCommand);
                    incomingToTargetCommandIds[incomingCommand.Id] = addedCommand.Id;
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
                incomingToTargetCommandIds[incomingCommand.Id] = existingCommand.Id;
            }

            foreach (var incomingGroup in incomingProject.Groups)
            {
                var translated = CloneGroup(incomingGroup);
                translated.CommandIds = CommandGroupOperations.RemapCommandIds(incomingGroup.CommandIds, incomingToTargetCommandIds);
                if (translated.ExecutionMode == CommandGroupExecutionMode.Sequential && translated.CommandIds.Any(id =>
                        existingProject.Commands.First(command => command.Id == id).Kind == CommandKind.Service))
                {
                    MessageBox.Show(this,
                        $"导入分组“{translated.Name}”映射到了本机的服务命令，因此不能作为顺序分组导入。请替换冲突命令，或先调整本机命令类型。",
                        "顺序分组无法合并", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                var existingGroupIndex = existingProject.Groups.FindIndex(group => SameGroupName(group.Name, incomingGroup.Name));
                if (existingGroupIndex < 0)
                {
                    if (targetProjects.SelectMany(project => project.Groups).Any(group => group.Id == translated.Id)) translated.Id = Guid.NewGuid();
                    existingProject.Groups.Add(translated);
                    continue;
                }

                var existingGroup = existingProject.Groups[existingGroupIndex];
                var groupChoice = MessageBox.Show(this,
                    $"项目“{existingProject.Name}”中已有同名分组。\n\n名称：{existingGroup.Name}\n本机模式：{GetGroupModeText(existingGroup.ExecutionMode)}\n导入模式：{GetGroupModeText(incomingGroup.ExecutionMode)}\n\n选择“是”以替换分组，选择“否”以保留本机分组，选择“取消”以停止导入。",
                    "分组名称冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (groupChoice == MessageBoxResult.Cancel) return false;
                if (groupChoice == MessageBoxResult.Yes)
                {
                    translated.Id = existingGroup.Id;
                    existingProject.Groups[existingGroupIndex] = translated;
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

    private static string GetGroupModeText(CommandGroupExecutionMode mode) => mode == CommandGroupExecutionMode.Sequential ? "顺序执行" : "同时执行";

    private static ProjectDefinition CloneProject(ProjectDefinition project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Directory = Path.GetFullPath(project.Directory),
        Commands = project.Commands.Select(CloneCommand).ToList(),
        Groups = project.Groups.Select(CloneGroup).ToList()
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

    private static CommandGroupDefinition CloneGroup(CommandGroupDefinition group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        ExecutionMode = group.ExecutionMode,
        CommandIds = group.CommandIds.ToList()
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
            SelectCommand(_selectedProject?.Commands.FirstOrDefault(command => command.Id == commandId));
        }
    }

    private void RefreshCommandList(CommandDefinition? selectedCommand)
    {
        _syncingCommandSelection = true;
        try
        {
            CommandList.ItemsSource = null;
            CommandList.ItemsSource = _selectedProject?.Commands;
            RebuildOutputCommands();
            RebuildGroupItems();
        }
        finally { _syncingCommandSelection = false; }
        SelectCommand(selectedCommand);
        UpdateEmptyStates();
    }

    private async void MoveCommandUp_Click(object sender, RoutedEventArgs e) => await MoveSelectedCommandAsync(-1);

    private async void MoveCommandDown_Click(object sender, RoutedEventArgs e) => await MoveSelectedCommandAsync(1);

    private async Task MoveSelectedCommandAsync(int offset)
    {
        if (_selectedProject is null || _selectedCommand is null) return;
        var project = _selectedProject;
        var command = _selectedCommand;
        var index = project.Commands.IndexOf(command);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= project.Commands.Count || !BeginWorkspaceChange()) return;

        try
        {
            project.Commands.RemoveAt(index);
            project.Commands.Insert(destination, command);
            RefreshCommandList(command);
            if (!await SaveWorkspaceAsync())
            {
                project.Commands.RemoveAt(destination);
                project.Commands.Insert(index, command);
                RefreshCommandList(command);
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private async void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || _selectedCommand is null) return;
        var project = _selectedProject;
        var command = _selectedCommand;
        if (_restartsInProgress.Contains(command.Id) ||
            (_sessions.TryGetValue(command.Id, out var activeSession) && !activeSession.Completion.IsCompleted))
        {
            MessageBox.Show(this, "请先停止这条命令，再删除保存的配置。", "命令仍在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"从项目“{project.Name}”删除命令“{command.Name}”吗？\n\n删除后无法从工作区恢复这条命令。",
                "删除命令", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!BeginWorkspaceChange()) return;

        try
        {
            var index = project.Commands.IndexOf(command);
            if (index < 0) return;
            var previousGroupMembers = project.Groups.ToDictionary(group => group.Id, group => group.CommandIds.ToList());
            project.Commands.RemoveAt(index);
            CommandGroupOperations.RemoveCommandReferences(project, command.Id);
            var nextSelection = project.Commands.Count == 0 ? null : project.Commands[Math.Min(index, project.Commands.Count - 1)];
            RefreshCommandList(nextSelection);
            if (!await SaveWorkspaceAsync())
            {
                project.Commands.Insert(index, command);
                foreach (var group in project.Groups) group.CommandIds = previousGroupMembers[group.Id];
                RefreshCommandList(command);
                return;
            }

            _checkedCommandIds.Remove(command.Id);
            _logs.Remove(command.Id);
            _pendingOutput.Clear(command.Id);
            _lastResults.Remove(command.Id);
            _runStartedAt.Remove(command.Id);
            _runVersions.Remove(command.Id);
            _sessions.Remove(command.Id);
            RefreshCommandSelection();
        }
        finally { EndWorkspaceChange(); }
    }

    private async void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenWorkspaceEditor() || _selectedProject is null) return;
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
            RefreshCommandList(editor.Result);
            if (!await SaveWorkspaceAsync())
            {
                project.Commands.Remove(editor.Result);
                RefreshCommandList(null);
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private async void EditCommand_Click(object sender, RoutedEventArgs e) => await EditSelectedCommandAsync();

    private async Task EditSelectedCommandAsync()
    {
        if (_selectedProject is null || _selectedCommand is null || !CanEditCommand(_selectedCommand)) return;
        var editor = new CommandEditorWindow(_selectedProject.Directory, _selectedCommand) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (_selectedProject.Commands.Any(command => command.Id != editor.Result.Id && SameCommandName(command.Name, editor.Result.Name)))
        {
            MessageBox.Show(this, "此项目已有同名命令，请使用其他名称。", "命令名称已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (editor.Result.Kind == CommandKind.Service)
        {
            var sequentialGroups = CommandGroupOperations.GetSequentialGroupsContaining(_selectedProject, editor.Result.Id);
            if (sequentialGroups.Count > 0)
            {
                MessageBox.Show(this,
                    $"服务命令不能留在顺序分组中。请先从以下分组移除此命令：\n\n{string.Join("、", sequentialGroups.Select(group => group.Name))}",
                    "顺序分组仅支持任务", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        if (!BeginWorkspaceChange()) return;
        try
        {
            var project = _selectedProject;
            var index = project.Commands.IndexOf(_selectedCommand);
            var previousCommand = project.Commands[index];
            project.Commands[index] = editor.Result;
            RefreshCommandList(editor.Result);
            if (!await SaveWorkspaceAsync())
            {
                project.Commands[index] = previousCommand;
                RefreshCommandList(previousCommand);
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

    private async void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || _selectedProject.Commands.Count == 0 || !CanOpenWorkspaceEditor()) return;
        var editor = new CommandGroupEditorWindow(_selectedProject.Commands) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (_selectedProject.Groups.Any(group => SameGroupName(group.Name, editor.Result.Name)))
        {
            MessageBox.Show(this, "此项目已有同名分组，请使用其他名称。", "分组名称已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!BeginWorkspaceChange()) return;
        try
        {
            var project = _selectedProject;
            project.Groups.Add(editor.Result);
            RebuildGroupItems();
            if (!await SaveWorkspaceAsync())
            {
                project.Groups.Remove(editor.Result);
                RebuildGroupItems();
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private async void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || sender is not Button { DataContext: CommandGroupItem item } || !CanOpenWorkspaceEditor()) return;
        var project = _selectedProject;
        var group = project.Groups.FirstOrDefault(candidate => candidate.Id == item.Group.Id);
        if (group is null) return;
        var editor = new CommandGroupEditorWindow(project.Commands, group) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        if (project.Groups.Any(candidate => candidate.Id != group.Id && SameGroupName(candidate.Name, editor.Result.Name)))
        {
            MessageBox.Show(this, "此项目已有同名分组，请使用其他名称。", "分组名称已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!BeginWorkspaceChange()) return;
        try
        {
            var index = project.Groups.IndexOf(group);
            project.Groups[index] = editor.Result;
            RebuildGroupItems();
            if (!await SaveWorkspaceAsync())
            {
                project.Groups[index] = group;
                RebuildGroupItems();
            }
            else
            {
                _groupStatuses.Remove(group.Id);
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || sender is not Button { DataContext: CommandGroupItem item } || !CanOpenWorkspaceEditor()) return;
        var project = _selectedProject;
        var group = project.Groups.FirstOrDefault(candidate => candidate.Id == item.Group.Id);
        if (group is null) return;
        if (MessageBox.Show(this, $"删除分组“{group.Name}”吗？\n\n分组中的命令不会被删除。",
                "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!BeginWorkspaceChange()) return;
        try
        {
            var index = project.Groups.IndexOf(group);
            project.Groups.RemoveAt(index);
            RebuildGroupItems();
            if (!await SaveWorkspaceAsync())
            {
                project.Groups.Insert(index, group);
                RebuildGroupItems();
            }
            else
            {
                _groupStatuses.Remove(group.Id);
            }
        }
        finally { EndWorkspaceChange(); }
    }

    private void RunGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || sender is not Button { DataContext: CommandGroupItem item } ||
            _workspaceChangeInProgress || _isStoppingForClose || !_canSave) return;
        var project = _selectedProject;
        var group = project.Groups.FirstOrDefault(candidate => candidate.Id == item.Group.Id);
        if (group is null || group.CommandIds.Count == 0) return;

        if (group.ExecutionMode == CommandGroupExecutionMode.Parallel)
        {
            RunParallelGroup(project, group);
            return;
        }

        StartSequentialGroup(project, group);
    }

    private void RunParallelGroup(ProjectDefinition project, CommandGroupDefinition group)
    {
        var commands = CommandGroupExecution.GetParallelReadyCommands(project, group, IsCommandBusy);
        var failures = new List<string>();
        var launched = new List<ParallelCommandExecution>();
        CommandDefinition? firstStarted = null;
        var version = _parallelGroupVersions.GetValueOrDefault(group.Id) + 1;
        _parallelGroupVersions[group.Id] = version;
        _groupStatuses.Remove(group.Id);
        foreach (var command in commands)
        {
            if (TryStartCommand(project, command, out var session, out var error))
            {
                firstStarted ??= command;
                launched.Add(new ParallelCommandExecution(command, session!.Completion));
                TrackParallelGroupSession(group.Id, command.Id, session);
            }
            else failures.Add($"{command.Name}：{error}");
        }

        if (firstStarted is not null)
        {
            SelectCommand(firstStarted);
            SetPage(MainPage.Output);
        }
        RefreshCommandSelection();
        if (launched.Count > 0) _ = MonitorParallelGroupAsync(group.Id, group.Name, version, launched);
        if (failures.Count > 0)
        {
            MessageBox.Show(this, $"以下命令未能启动：\n\n{string.Join("\n", failures)}", "分组启动未全部完成", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TrackParallelGroupSession(Guid groupId, Guid commandId, CommandRunSession session)
    {
        if (!_parallelGroupSessions.TryGetValue(groupId, out var sessions))
        {
            _parallelGroupSessions[groupId] = sessions = [];
        }
        sessions[commandId] = session;
        _ = RemoveParallelGroupSessionWhenCompleteAsync(groupId, commandId, session);
    }

    private async Task RemoveParallelGroupSessionWhenCompleteAsync(Guid groupId, Guid commandId, CommandRunSession session)
    {
        try { await session.Completion; }
        catch { /* CompleteRunAsync reports unexpected observation failures. */ }
        await Dispatcher.InvokeAsync(() =>
        {
            if (!_parallelGroupSessions.TryGetValue(groupId, out var sessions) ||
                !sessions.TryGetValue(commandId, out var current) || !ReferenceEquals(current, session)) return;
            sessions.Remove(commandId);
            if (sessions.Count == 0) _parallelGroupSessions.Remove(groupId);
        });
    }

    private async Task MonitorParallelGroupAsync(Guid groupId, string groupName, long version, IReadOnlyList<ParallelCommandExecution> launched)
    {
        ParallelGroupObservationResult observation;
        try
        {
            observation = await CommandGroupExecution.ObserveParallelAsync(launched);
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_parallelGroupVersions.GetValueOrDefault(groupId) == version)
                    _groupStatuses[groupId] = $"状态异常 · {exception.Message}";
                RefreshCommandSelection();
            });
            return;
        }

        if (observation.Outcome is ParallelGroupObservationOutcome.NoTaskCommands) return;
        if (observation.Outcome == ParallelGroupObservationOutcome.TasksSucceeded)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_parallelGroupVersions.GetValueOrDefault(groupId) != version) return;
                var activeServices = _parallelGroupSessions.GetValueOrDefault(groupId)?.Values.Count(session => !session.Completion.IsCompleted) ?? 0;
                _groupStatuses[groupId] = activeServices > 0
                    ? $"任务已成功 · {activeServices} 个服务继续运行"
                    : "已完成 · 所有任务成功";
                RefreshCommandSelection();
            });
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_parallelGroupVersions.GetValueOrDefault(groupId) == version)
            {
                _groupStatuses[groupId] = observation.Outcome == ParallelGroupObservationOutcome.CommandFailed
                    ? $"执行失败 · 正在停止本分组启动的其他命令"
                    : "执行已停止 · 正在结束本分组启动的其他命令";
            }
            RefreshCommandSelection();
        });

        var stopFailures = await StopParallelGroupSessionsAsync(groupId);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_parallelGroupVersions.GetValueOrDefault(groupId) == version)
            {
                _groupStatuses[groupId] = observation.Outcome == ParallelGroupObservationOutcome.CommandFailed
                    ? $"执行失败 · {observation.Command?.Name} · 退出码 {observation.RunResult?.ExitCode}"
                    : $"已取消 · {observation.Command?.Name} 已停止";
            }
            RefreshCommandSelection();
            if (observation.Outcome == ParallelGroupObservationOutcome.CommandFailed && !_isStoppingForClose)
            {
                var cleanup = stopFailures.Count == 0
                    ? "已停止此分组启动的其他运行中命令。"
                    : $"以下命令未能确认停止：\n{string.Join("\n", stopFailures)}";
                MessageBox.Show(this,
                    $"分组“{groupName}”中的命令“{observation.Command?.Name}”执行失败，退出码为 {observation.RunResult?.ExitCode}。\n\n{cleanup}",
                    "分组执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private async Task<List<string>> StopParallelGroupSessionsAsync(Guid groupId)
    {
        var sessions = await Dispatcher.InvokeAsync(() =>
            _parallelGroupSessions.GetValueOrDefault(groupId)?.Values
                .Where(session => !session.Completion.IsCompleted)
                .Distinct()
                .ToArray() ?? []);
        var failures = new List<string>();
        foreach (var session in sessions)
        {
            try
            {
                await session.StopAsync();
                await session.Completion;
            }
            catch (Exception exception)
            {
                failures.Add(exception.Message);
            }
        }
        return failures;
    }

    private void StartSequentialGroup(ProjectDefinition project, CommandGroupDefinition group)
    {
        if (_sequentialGroupCancellations.ContainsKey(group.Id)) return;
        var commands = CommandGroupExecution.ResolveCommands(project, group).ToArray();
        if (commands.Length == 0 || commands.Any(command => IsCommandBusy(command.Id)))
        {
            MessageBox.Show(this, "顺序分组中的所有命令都必须处于空闲状态。", "分组暂不可运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (commands.Any(command => command.Kind == CommandKind.Service))
        {
            MessageBox.Show(this, "顺序执行仅支持任务命令。请编辑分组并移除服务命令。", "顺序分组包含服务", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _sequentialGroupCancellations[group.Id] = cancellation;
        foreach (var command in commands) _reservedCommandIds.Add(command.Id);
        _groupStatuses[group.Id] = $"正在执行 1 / {commands.Length}";
        var task = RunSequentialGroupAsync(project.Id, group.Id, commands, cancellation);
        _sequentialGroupTasks[group.Id] = task;
        SelectCommand(commands[0]);
        SetPage(MainPage.Output);
        RefreshCommandSelection();
    }

    private async Task RunSequentialGroupAsync(Guid projectId, Guid groupId, CommandDefinition[] commands, CancellationTokenSource cancellation)
    {
        var startedCount = 0;
        try
        {
            var result = await CommandGroupExecution.RunSequentialAsync(commands, async (command, token) =>
            {
                token.ThrowIfCancellationRequested();
                return await Dispatcher.InvokeAsync(() =>
                {
                    startedCount++;
                    _groupStatuses[groupId] = $"正在执行 {startedCount} / {commands.Length} · {command.Name}";
                    if (!TryStartCommand(Projects.First(project => project.Id == projectId), command, out var session, out var error, allowReserved: true))
                    {
                        throw new InvalidOperationException(error);
                    }
                    SelectCommand(command);
                    RefreshCommandSelection();
                    return session!.Completion;
                }).Task.Unwrap();
            }, cancellation.Token);

            await Dispatcher.InvokeAsync(() =>
            {
                _groupStatuses[groupId] = result.StopReason switch
                {
                    SequentialGroupStopReason.Completed => $"已完成 · {result.CompletedCount} 条任务成功",
                    SequentialGroupStopReason.CommandFailed => $"已终止 · {result.LastCommand?.Name} 执行失败",
                    SequentialGroupStopReason.CommandStopped => $"已终止 · {result.LastCommand?.Name} 已停止",
                    SequentialGroupStopReason.StartFailed => $"已终止 · {result.LastCommand?.Name} 启动失败",
                    _ => "已取消 · 后续命令未运行"
                };
                if (result.StartException is not null && !_isStoppingForClose)
                {
                    MessageBox.Show(this, result.StartException.Message, "顺序分组启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var command in commands) _reservedCommandIds.Remove(command.Id);
                _sequentialGroupCancellations.Remove(groupId);
                _sequentialGroupTasks.Remove(groupId);
                cancellation.Dispose();
                RefreshCommandSelection();
            });
        }
    }

    private bool CanOpenWorkspaceEditor() => !_workspaceChangeInProgress && _restartsInProgress.Count == 0 &&
        _sequentialGroupCancellations.Count == 0 && !_isStoppingForClose && _canSave;

    private static bool SameGroupName(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || _selectedCommand is null) return;
        if (StartCommand(_selectedProject, _selectedCommand)) SetPage(MainPage.Output);
    }

    private void RunCheckedCommands_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || !_canSave || _workspaceChangeInProgress || _isStoppingForClose) return;
        var project = _selectedProject;
        var commands = project.Commands.Where(command => _checkedCommandIds.Contains(command.Id) &&
            !IsCommandBusy(command.Id)).ToArray();
        CommandDefinition? firstStarted = null;
        foreach (var command in commands)
        {
            if (StartCommand(project, command)) firstStarted ??= command;
        }
        if (firstStarted is not null)
        {
            SelectCommand(firstStarted);
            SetPage(MainPage.Output);
        }
        RefreshCommandSelection();
    }

    private async void RestartCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || _selectedCommand is null) return;
        var projectId = _selectedProject.Id;
        var commandId = _selectedCommand.Id;
        if (_reservedCommandIds.Contains(commandId) || !_restartsInProgress.Add(commandId)) return;
        RefreshCommandSelection();
        try
        {
            if (_sessions.TryGetValue(commandId, out var session) && !session.Completion.IsCompleted)
            {
                RunStateText.Text = "正在重启 · 停止中";
                OutputRunStateText.Text = RunStateText.Text;
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

    private bool StartCommand(ProjectDefinition project, CommandDefinition command)
    {
        if (TryStartCommand(project, command, out _, out var error)) return true;
        if (!string.IsNullOrWhiteSpace(error))
        {
            MessageBox.Show(this, error, "命令启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return false;
    }

    private bool TryStartCommand(ProjectDefinition project, CommandDefinition command, out CommandRunSession? session, out string error, bool allowReserved = false)
    {
        session = null;
        error = string.Empty;
        if (!_canSave || _workspaceChangeInProgress || _isStoppingForClose) return false;
        if (!allowReserved && _reservedCommandIds.Contains(command.Id))
        {
            error = "这条命令正在等待顺序分组执行。";
            return false;
        }
        var workingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? project.Directory : command.WorkingDirectory;
        RunLogWriter? logWriter = null;
        try
        {
            if (!_logStoreAvailable) throw new IOException("日志目录或设置不可用。");
            logWriter = _runLogStore.Begin(project.Id, command.Id, DateTimeOffset.Now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logWarnings[command.Id] = $"本次日志未保存：{exception.Message}";
            LogStatusText.Text = _logWarnings[command.Id];
        }
        try
        {
            var nextVersion = _runVersions.GetValueOrDefault(command.Id) + 1;
            var started = _runner.Start(command.Id, command.Command, workingDirectory,
                (_, output) =>
                {
                    if (logWriter is not null)
                    {
                        try { _runLogStore.Append(logWriter, output); }
                        catch (Exception) { logWriter.Info.WriteFailed = true; }
                        if ((logWriter.Info.WriteFailed || logWriter.Info.Truncated) &&
                            Interlocked.Exchange(ref logWriter.WarningReported, 1) == 0)
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _logWarnings[command.Id] = logWriter.Info.Truncated ? "日志达到容量上限，本批次后续输出未保存。" : "日志写入失败，本批次输出不完整。";
                                if (_selectedCommand?.Id == command.Id) LogStatusText.Text = _logWarnings[command.Id];
                            }));
                        }
                    }
                    QueueOutput(command.Id, nextVersion, output);
                });
            _runVersions[command.Id] = nextVersion;
            if (logWriter is not null) _runLogWriters[command.Id] = logWriter;
            _lastResults.Remove(command.Id);
            _runStartedAt[command.Id] = DateTimeOffset.Now;
            GetLogs(command.Id).Clear();
            if (_selectedCommand?.Id == command.Id) RenderSelectedOutput(command, scrollToEnd: true);
            _sessions[command.Id] = started;
            session = started;
            RefreshRunHistory(selectCurrent: true);
            RefreshCommandSelection();
            _logFinalizations[command.Id] = CompleteRunAsync(command.Id, started, logWriter);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (logWriter is not null)
            {
                try { _runLogStore.Discard(logWriter); }
                catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
                {
                    _logWarnings[command.Id] = $"启动失败批次的日志未能清理：{cleanupFailure.Message}";
                }
            }
            RefreshCommandSelection();
            error = exception.Message;
            return false;
        }
    }

    private async Task CompleteRunAsync(Guid commandId, CommandRunSession session, RunLogWriter? logWriter)
    {
        try
        {
            var result = await session.Completion;
            if (logWriter is not null)
            {
                try { _runLogStore.Complete(logWriter, result); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logWarnings[commandId] = $"日志完成写入失败：{exception.Message}";
                }
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (logWriter is not null && _runLogWriters.TryGetValue(commandId, out var currentWriter) && ReferenceEquals(currentWriter, logWriter))
                    _runLogWriters.Remove(commandId);
                if (_sessions.TryGetValue(commandId, out var current) && ReferenceEquals(current, session)) _sessions.Remove(commandId);
                if (Projects.Any(project => project.Commands.Any(command => command.Id == commandId))) _lastResults[commandId] = result;
                if (_selectedCommand?.Id == commandId) RefreshRunHistory(selectCurrent: false);
                RefreshCommandSelection();
            });
        }
        catch (Exception exception)
        {
            if (logWriter is not null)
            {
                try { _runLogStore.Complete(logWriter, null); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    _logWarnings[commandId] = $"日志完成写入失败：{failure.Message}";
                }
            }
            await Dispatcher.InvokeAsync(() =>
            {
                RunStateText.Text = "进程异常";
                RunStateText.ToolTip = exception.Message;
            });
        }
        finally
        {
            if (_logFinalizations.TryGetValue(commandId, out var pending) && pending.IsCompleted)
                _logFinalizations.Remove(commandId);
        }
    }

    private async void StopCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null || !_sessions.TryGetValue(_selectedCommand.Id, out var session)) return;
        var stoppedCommandId = _selectedCommand.Id;
        RunStateText.Text = "正在停止 · Ctrl+C";
        OutputRunStateText.Text = RunStateText.Text;
        var outputItem = _outputCommands.FirstOrDefault(item => item.Command.Id == stoppedCommandId);
        if (outputItem is not null) outputItem.StatusText = RunStateText.Text;
        StopButton.IsEnabled = false;
        OutputStopButton.IsEnabled = false;
        try
        {
            var stopResult = await session.StopAsync();
            await session.Completion;
            if (_selectedCommand?.Id == stoppedCommandId)
            {
                RunStateText.Text = stopResult == CommandStopResult.ForceTerminated ? "已强制停止" : "已停止";
                OutputRunStateText.Text = RunStateText.Text;
                StopButton.IsEnabled = false;
                OutputStopButton.IsEnabled = false;
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

    private void CopySelectedOutput_Click(object sender, RoutedEventArgs e)
    {
        if (OutputTextBox.SelectionLength == 0) return;
        try
        {
            OutputTextBox.Copy();
            SaveStatusText.Text = "已复制所选输出";
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            MessageBox.Show(this, $"TalosDesk 无法访问剪贴板。请稍后重试。\n\n{exception.Message}",
                "复制输出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommand is null || SelectedHistoricalRun is not null) return;
        GetLogs(_selectedCommand.Id).Clear();
        _pendingOutput.Clear(_selectedCommand.Id);
        RenderSelectedOutput(_selectedCommand, scrollToEnd: true);
        CopySelectedOutputButton.IsEnabled = false;
        EmptyOutputHint.Visibility = Visibility.Visible;
    }

    private RunLogInfo? SelectedHistoricalRun => (RunHistoryComboBox.SelectedItem as RunHistoryItem)?.Info;

    private void RefreshRunHistory(bool selectCurrent)
    {
        if (RunHistoryComboBox is null) return;
        var selectedRunId = selectCurrent ? null : SelectedHistoricalRun?.RunId;
        _syncingRunHistory = true;
        try
        {
            _runHistory.Clear();
            if (_selectedProject is null || _selectedCommand is null)
            {
                RunHistoryComboBox.SelectedItem = null;
                return;
            }
            _runHistory.Add(new RunHistoryItem("当前显示", null));
            foreach (var info in _runLogStore.GetRuns(_selectedProject.Id, _selectedCommand.Id))
                _runHistory.Add(new RunHistoryItem($"{info.StartedAt.ToLocalTime():MM-dd HH:mm:ss} · {GetRunStateLabel(info.State)}", info));
            var previous = selectedRunId.HasValue ? _runHistory.FirstOrDefault(item => item.Info?.RunId == selectedRunId) : null;
            RunHistoryComboBox.SelectedItem = selectCurrent ? _runHistory[0] : previous ?? (_logs.TryGetValue(_selectedCommand.Id, out var lines) && lines.Count > 0 ||
                _sessions.ContainsKey(_selectedCommand.Id) ? _runHistory[0] : _runHistory.Skip(1).FirstOrDefault() ?? _runHistory[0]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogStatusText.Text = $"无法读取历史日志：{exception.Message}";
        }
        finally { _syncingRunHistory = false; }
        ShowSelectedRun();
    }

    private void RunHistoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingRunHistory) ShowSelectedRun();
    }

    private void HistoryStreamComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingRunHistory) ShowSelectedRun();
    }

    private void ShowSelectedRun()
    {
        if (OutputTextBox is null) return;
        var info = SelectedHistoricalRun;
        if (info is null)
        {
            RenderSelectedOutput(_selectedCommand, scrollToEnd: true);
            ClearOutputButton.IsEnabled = _selectedCommand is not null;
            if (_selectedCommand is not null)
            {
                OutputRunStateText.Text = GetCommandStatusText(_selectedCommand);
                RunStartedText.Text = _runStartedAt.TryGetValue(_selectedCommand.Id, out var startedAt)
                    ? $"开始时间：{startedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : string.Empty;
                LogStatusText.Text = _logWarnings.GetValueOrDefault(_selectedCommand.Id) ?? string.Empty;
            }
            return;
        }
        try
        {
            var stream = HistoryStreamComboBox.SelectedIndex == 1 ? "stderr" : "stdout";
            var lines = _runLogStore.ReadTail(info, stream);
            _syncingOutputText = true;
            OutputTextBox.Text = string.Join(Environment.NewLine, lines);
            OutputTextBox.ScrollToEnd();
            EmptyOutputHint.Text = lines.Count == 0 ? "该批次的此输出通道没有内容。" : string.Empty;
            EmptyOutputHint.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            OutputRunStateText.Text = GetRunStateLabel(info.State);
            RunStartedText.Text = $"开始时间：{info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            LogStatusText.Text = info.Truncated ? "该批次日志已达到容量上限，后续输出未保存。" :
                info.WriteFailed ? "该批次日志写入失败，内容不完整。" : string.Empty;
            ClearOutputButton.IsEnabled = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStatusText.Text = $"无法读取历史日志：{exception.Message}";
        }
        finally { _syncingOutputText = false; }
    }

    private void SaveLogSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(LogLimitMbTextBox.Text, out var mb) || mb is < 1 or > 102400 ||
            !int.TryParse(LogRetentionDaysTextBox.Text, out var days) || days is < 1 or > 3650)
        {
            MessageBox.Show(this, "日志容量请输入 1–102400 MB，保留时间请输入 1–3650 天。", "日志设置无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _runLogStore.UpdateSettings(new RunLogSettings(mb * 1024 * 1024, days));
            if (!_logStoreAvailable) _runLogStore.Initialize();
            _logStoreAvailable = true;
            _logInitError = null;
            RefreshRunHistory(selectCurrent: false);
            LogStatusText.Text = "日志设置已保存。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"无法保存日志设置。\n\n{exception.Message}", "日志设置未保存", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateLogPathDisplay()
    {
        LogPathText.Text = $"日志位置：{_runLogStore.RootPath}";
        LogPathText.ToolTip = _runLogStore.RootPath;
    }

    private bool CanChangeLogLocation()
    {
        if (_sessions.Values.Any(session => !session.Completion.IsCompleted) || _runLogWriters.Count > 0 ||
            _logFinalizations.Values.Any(task => !task.IsCompleted))
        {
            MessageBox.Show(this, "请等待所有命令和日志写入结束后再更改日志位置。", "日志正在写入",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    private void ChangeLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeLogLocation()) return;
        var picker = new OpenFolderDialog { Title = "选择日志保存文件夹", Multiselect = false };
        var currentParent = Path.GetDirectoryName(_runLogStore.RootPath);
        if (currentParent is not null && Directory.Exists(currentParent)) picker.InitialDirectory = currentParent;
        if (picker.ShowDialog(this) == true) ApplyLogLocation(picker.FolderName);
    }

    private void ResetLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CanChangeLogLocation()) ApplyLogLocation(null);
    }

    private void ApplyLogLocation(string? parentDirectory)
    {
        try
        {
            var oldFolder = _runLogStore.ChangeLocation(parentDirectory);
            UpdateLogPathDisplay();
            if (!_logStoreAvailable) _runLogStore.Initialize();
            _logStoreAvailable = true;
            _logInitError = null;
            RefreshRunHistory(selectCurrent: false);
            LogStatusText.Text = oldFolder is null ? "日志位置已更新，历史日志已迁移。" :
                $"日志位置已更新，但旧目录未能删除，请手工清理：{oldFolder}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            UpdateLogPathDisplay();
            MessageBox.Show(this, $"无法更改日志位置。\n\n{exception.Message}", "日志位置未更改", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GetRunStateLabel(string state) => state switch
    {
        "Running" => "运行中",
        "Succeeded" => "已完成",
        "Failed" => "运行失败",
        "Stopped" => "已停止",
        "Interrupted" => "意外中断",
        _ => "状态未知"
    };

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_runLogStore.RootPath);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(_runLogStore.RootPath);
            Process.Start(start);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"无法打开日志目录。\n\n{exception.Message}", "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "清理此工作区所有已结束批次的日志吗？运行中的日志会保留。", "清理历史日志",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _runLogStore.ClearHistory();
            RefreshRunHistory(selectCurrent: true);
            LogStatusText.Text = "已清理历史日志。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"部分日志无法清理。\n\n{exception.Message}", "清理未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        var activeSequences = _sequentialGroupCancellations.Count;
        if (active.Length == 0 && activeSequences == 0 && _logFinalizations.Values.All(task => task.IsCompleted)) return;

        if (active.Length == 0 && activeSequences == 0)
        {
            e.Cancel = true;
            await Task.WhenAll(_logFinalizations.Values.ToArray());
            _allowClose = true;
            Close();
            return;
        }

        e.Cancel = true;
        var choice = MessageBox.Show(this,
            activeSequences > 0
                ? $"还有 {active.Length} 条命令正在运行，{activeSequences} 个顺序分组尚未完成。取消后续步骤、停止运行中的命令并退出吗？"
                : $"还有 {active.Length} 条命令正在运行。停止这些命令并退出吗？",
            "命令仍在运行", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return;

        _isStoppingForClose = true;
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        try
        {
            foreach (var cancellation in _sequentialGroupCancellations.Values.ToArray()) cancellation.Cancel();
            var groupTasks = _sequentialGroupTasks.Values.ToArray();
            await Task.WhenAll(active.Select(session => session.StopAsync()));
            await Task.WhenAll(active.Select(session => session.Completion));
            await Task.WhenAll(groupTasks);
            await Task.WhenAll(_logFinalizations.Values.ToArray());
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
            if (!await EnsureWorkspaceUnchangedAsync(showDialog: true)) return false;
            await _store.SaveAsync(new WorkspaceConfiguration { Projects = Projects.ToList() });
            _workspaceRevision = await _store.GetRevisionAsync();
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

    private async Task<bool> EnsureWorkspaceUnchangedAsync(bool showDialog)
    {
        if (_workspaceRevision is null) return true;
        var currentRevision = await _store.GetRevisionAsync();
        if (currentRevision == _workspaceRevision.Value) return true;

        _canSave = false;
        _saveFailed = true;
        SaveStatusText.Text = "外部配置已变更";
        RefreshCommandSelection();
        if (showDialog && !_externalWorkspaceChangeReported)
        {
            _externalWorkspaceChangeReported = true;
            MessageBox.Show(this,
                $"工作区文件已被另一个程序修改。为避免覆盖新内容，当前窗口已停止保存。请关闭后重新打开 TalosDesk。\n\n{_store.FilePath}",
                "检测到外部配置变化", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }

    private bool BeginWorkspaceChange()
    {
        if (!_canSave || _workspaceChangeInProgress || _restartsInProgress.Count > 0 ||
            _sequentialGroupCancellations.Count > 0 || _isStoppingForClose) return false;
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
        var selectedOutput = new List<CommandOutput>();
        var selectedOutputTrimmed = false;
        foreach (var entry in _pendingOutput.Take(500))
        {
            if (!_runVersions.TryGetValue(entry.CommandId, out var currentVersion) || currentVersion != entry.RunVersion) continue;
            var trimmed = AppendOutput(entry.CommandId, entry.Output);
            if (_selectedCommand?.Id == entry.CommandId)
            {
                selectedOutput.Add(entry.Output);
                selectedOutputTrimmed |= trimmed;
            }
        }

        if (selectedOutput.Count > 0) AppendSelectedOutputBatch(selectedOutput, selectedOutputTrimmed);
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

    private bool AppendOutput(Guid commandId, CommandOutput output)
    {
        var logs = GetLogs(commandId);
        logs.Add(output);
        var trimmed = false;
        while (logs.Count > 10_000)
        {
            logs.RemoveAt(0);
            trimmed = true;
        }
        return trimmed;
    }

    private static string FormatOutputLine(CommandOutput output) =>
        $"{(output.Stream == "stderr" ? "诊断输出" : "标准输出")}  {output.Text}";

    private void RenderSelectedOutput(CommandDefinition? command, bool scrollToEnd = false, double? verticalOffset = null)
    {
        _syncingOutputText = true;
        try
        {
            if (command is null)
            {
                _renderedOutputCommandId = null;
                _renderedOutputLogs = null;
                OutputTextBox.Clear();
                _outputFollowEnabled = true;
            }
            else
            {
                var logs = GetLogs(command.Id);
                _renderedOutputCommandId = command.Id;
                _renderedOutputLogs = logs;
                OutputTextBox.Text = string.Join(Environment.NewLine, logs.Select(FormatOutputLine));
                if (scrollToEnd)
                {
                    OutputTextBox.CaretIndex = OutputTextBox.Text.Length;
                    OutputTextBox.ScrollToEnd();
                    _outputFollowEnabled = true;
                }
                else if (verticalOffset.HasValue)
                {
                    OutputTextBox.ScrollToVerticalOffset(verticalOffset.Value);
                }
            }

            _outputViewNeedsResync = false;
            OutputTextBox.Select(scrollToEnd ? OutputTextBox.Text.Length : 0, 0);
            CopySelectedOutputButton.IsEnabled = false;
        }
        finally
        {
            _syncingOutputText = false;
        }
    }

    private bool IsOutputAtEnd() =>
        OutputTextBox.ExtentHeight <= OutputTextBox.ViewportHeight ||
        OutputTextBox.VerticalOffset + OutputTextBox.ViewportHeight >= OutputTextBox.ExtentHeight - 2;

    private void AppendSelectedOutputBatch(IReadOnlyList<CommandOutput> entries, bool trimmed)
    {
        if (_selectedCommand is null || SelectedHistoricalRun is not null || _renderedOutputCommandId != _selectedCommand.Id) return;

        var selectionStart = OutputTextBox.SelectionStart;
        var selectionLength = OutputTextBox.SelectionLength;
        var verticalOffset = OutputTextBox.VerticalOffset;
        var shouldFollow = selectionLength == 0 && _outputFollowEnabled;

        if (trimmed && selectionLength == 0)
        {
            RenderSelectedOutput(_selectedCommand, shouldFollow, shouldFollow ? null : verticalOffset);
        }
        else
        {
            _syncingOutputText = true;
            try
            {
                var appendedText = string.Join(Environment.NewLine, entries.Select(FormatOutputLine));
                if (OutputTextBox.Text.Length > 0) appendedText = Environment.NewLine + appendedText;
                OutputTextBox.AppendText(appendedText);

                if (selectionLength > 0)
                {
                    OutputTextBox.Select(selectionStart, selectionLength);
                    OutputTextBox.ScrollToVerticalOffset(verticalOffset);
                    if (trimmed) _outputViewNeedsResync = true;
                }
                else if (shouldFollow)
                {
                    OutputTextBox.CaretIndex = OutputTextBox.Text.Length;
                    OutputTextBox.ScrollToEnd();
                }
                else
                {
                    // 折叠光标若停在旧文本末尾，TextBox 会在后续布局时把它重新滚入视野。
                    // 未选中文本时把光标留在开头，并单独恢复用户正在阅读的位置。
                    OutputTextBox.Select(0, 0);
                    OutputTextBox.ScrollToVerticalOffset(verticalOffset);
                }
            }
            finally
            {
                _syncingOutputText = false;
            }
        }

        EmptyOutputHint.Visibility = Visibility.Collapsed;
        CopySelectedOutputButton.IsEnabled = OutputTextBox.SelectionLength > 0;
    }

    private void OutputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingOutputText) return;
        CopySelectedOutputButton.IsEnabled = OutputTextBox.SelectionLength > 0;
        if (OutputTextBox.SelectionLength != 0 || !_outputViewNeedsResync || _selectedCommand is null) return;

        var verticalOffset = OutputTextBox.VerticalOffset;
        RenderSelectedOutput(_selectedCommand, scrollToEnd: false, verticalOffset);
    }

    private void OutputTextBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingOutputText) return;
        _outputFollowEnabled = IsOutputAtEnd();
    }

    private void UpdateEmptyStates()
    {
        EmptyProjectsHint.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyCommandsHint.Visibility = _selectedProject is not null && _selectedProject.Commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyGroupsHint.Visibility = _selectedProject is not null && _selectedProject.Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyOutputCommandsHint.Visibility = _selectedProject is not null && _selectedProject.Commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewDetails.Visibility = _selectedProject is null ? Visibility.Collapsed : Visibility.Visible;
        OverviewEmptyHint.Visibility = _selectedProject is null ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedCommand is null) EmptyOutputHint.Visibility = Visibility.Visible;
    }

}

public sealed record RunHistoryItem(string Label, RunLogInfo? Info);

public sealed class OutputCommandItem(CommandDefinition command) : INotifyPropertyChanged
{
    private string _statusText = "空闲";

    public CommandDefinition Command { get; } = command;
    public string Name => Command.Name;
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CommandGroupItem(CommandGroupDefinition group) : INotifyPropertyChanged
{
    private string _memberPath = string.Empty;
    private string _memberCountText = "0 条命令";
    private string _statusText = "空闲";
    private bool _canRun;
    private bool _canEdit;

    public CommandGroupDefinition Group { get; } = group;
    public string Name => Group.Name;
    public string MemberPath { get => _memberPath; private set => SetField(ref _memberPath, value, nameof(MemberPath)); }
    public string MemberCountText { get => _memberCountText; private set => SetField(ref _memberCountText, value, nameof(MemberCountText)); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value, nameof(StatusText)); }
    public bool CanRun { get => _canRun; private set => SetField(ref _canRun, value, nameof(CanRun)); }
    public bool CanEdit { get => _canEdit; private set => SetField(ref _canEdit, value, nameof(CanEdit)); }

    public void Update(IReadOnlyList<CommandDefinition> commands, string status, bool canRun, bool canEdit)
    {
        MemberPath = commands.Count == 0 ? "执行轨道为空" : string.Join("  →  ", commands.Select(command => command.Name));
        MemberCountText = $"{commands.Count} 条命令";
        StatusText = status;
        CanRun = canRun;
        CanEdit = canEdit;
    }

    private void SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
