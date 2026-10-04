using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

public partial class CommandGroupEditorWindow : Window
{
    private readonly Guid _groupId;

    public CommandGroupEditorWindow(IReadOnlyList<CommandDefinition> commands, CommandGroupDefinition? existing = null)
    {
        InitializeComponent();
        WindowPlacementController.Attach(this);
        SizeChanged += (_, _) =>
        {
            var stacked = ActualWidth < 760;
            MemberLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 14);
            MemberLayout.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(SelectedMemberPanel, stacked ? 0 : 2);
            Grid.SetRow(SelectedMemberPanel, stacked ? 1 : 0);
            SelectedMemberPanel.Margin = new Thickness(0, stacked ? 12 : 0, 0, 0);
        };
        _groupId = existing?.Id ?? Guid.Empty;
        var order = existing?.CommandIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index) ?? [];
        foreach (var command in commands)
        {
            Choices.Add(new GroupCommandChoice(command) { IsSelected = order.ContainsKey(command.Id) });
        }

        foreach (var choice in Choices.Where(choice => choice.IsSelected).OrderBy(choice => order[choice.Command.Id]))
        {
            SelectedChoices.Add(choice);
        }

        if (existing is not null)
        {
            HeadingText.Text = "编辑分组";
            NameBox.Text = existing.Name;
            ModeBox.SelectedIndex = existing.ExecutionMode == CommandGroupExecutionMode.Sequential ? 1 : 0;
        }

        DataContext = this;
        UpdateModeHint();
    }

    public ObservableCollection<GroupCommandChoice> Choices { get; } = [];
    public ObservableCollection<GroupCommandChoice> SelectedChoices { get; } = [];
    public CommandGroupDefinition? Result { get; private set; }

    private void MemberSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: GroupCommandChoice choice }) return;
        if (choice.IsSelected && !SelectedChoices.Contains(choice)) SelectedChoices.Add(choice);
        else if (!choice.IsSelected) SelectedChoices.Remove(choice);
        UpdateMoveButtons();
    }

    private void SelectedCommandsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMoveButtons();

    private void MoveMemberUp_Click(object sender, RoutedEventArgs e) => MoveSelectedMember(-1);
    private void MoveMemberDown_Click(object sender, RoutedEventArgs e) => MoveSelectedMember(1);

    private void MoveSelectedMember(int offset)
    {
        if (SelectedCommandsList.SelectedItem is not GroupCommandChoice choice) return;
        var index = SelectedChoices.IndexOf(choice);
        var destination = index + offset;
        if (destination < 0 || destination >= SelectedChoices.Count) return;
        SelectedChoices.Move(index, destination);
        SelectedCommandsList.SelectedItem = choice;
        UpdateMoveButtons();
    }

    private void UpdateMoveButtons()
    {
        var index = SelectedCommandsList.SelectedItem is GroupCommandChoice choice ? SelectedChoices.IndexOf(choice) : -1;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < SelectedChoices.Count - 1;
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateModeHint();
    }

    private void UpdateModeHint()
    {
        var sequential = ModeBox.SelectedIndex == 1;
        OrderHintText.Text = sequential ? "前一条成功完成后才运行下一条。" : "此顺序用于发起命令。";
        ModeHintText.Text = sequential
            ? "顺序执行仅支持任务；失败或停止后不会运行后续命令。"
            : "同时执行会跳过已占用命令，并按轨道顺序发起其余命令。";
    }

    private void SaveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            AppMessageDialog.Show(this, "请填写分组名称。", "分组信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SelectedChoices.Count == 0)
        {
            AppMessageDialog.Show(this, "请至少选择一条命令。", "分组没有成员", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mode = ModeBox.SelectedIndex == 1 ? CommandGroupExecutionMode.Sequential : CommandGroupExecutionMode.Parallel;
        var services = SelectedChoices.Where(choice => choice.Command.Kind == CommandKind.Service).Select(choice => choice.Command.Name).ToArray();
        if (mode == CommandGroupExecutionMode.Sequential && services.Length > 0)
        {
            AppMessageDialog.Show(this, $"顺序执行仅支持任务命令。请移除以下服务：\n\n{string.Join("、", services)}", "顺序分组包含服务", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = new CommandGroupDefinition
        {
            Id = _groupId == Guid.Empty ? Guid.NewGuid() : _groupId,
            Name = NameBox.Text.Trim(),
            ExecutionMode = mode,
            CommandIds = SelectedChoices.Select(choice => choice.Command.Id).ToList()
        };
        DialogResult = true;
    }
}

public sealed class GroupCommandChoice(CommandDefinition command) : INotifyPropertyChanged
{
    private bool _isSelected;
    public CommandDefinition Command { get; } = command;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
