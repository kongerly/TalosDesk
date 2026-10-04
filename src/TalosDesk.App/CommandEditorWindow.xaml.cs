using System.IO;
using System.Windows;
using Microsoft.Win32;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

public partial class CommandEditorWindow : Window
{
    private readonly Guid _commandId;
    private readonly List<CommandEnvironmentVariable> _environmentVariables;

    public CommandEditorWindow(string projectDirectory, CommandDefinition? existing = null)
    {
        InitializeComponent();
        WindowPlacementController.Attach(this);
        _environmentVariables = existing?.EnvironmentVariables.Select(variable => variable.Clone()).ToList() ?? [];
        RefreshEnvironmentList();
        WorkingDirectoryBox.Text = existing?.WorkingDirectory ?? projectDirectory;
        CommandBox.WorkingDirectory = WorkingDirectoryBox.Text;
        RefreshProbeFields();
        if (existing is null) return;

        _commandId = existing.Id;
        HeadingText.Text = "编辑命令";
        NameBox.Text = existing.Name;
        PurposeBox.Text = existing.Purpose;
        CommandBox.Text = existing.Command;
        KindBox.SelectedIndex = existing.Kind == CommandKind.Service ? 1 : 0;
        if (existing.TcpProbe is { } probe)
        {
            ProbeAddressBox.SelectedIndex = probe.Address == "::1" ? 1 : 0;
            ProbePortBox.Text = probe.Port.ToString();
            ProbeIntervalBox.Text = probe.IntervalSeconds.ToString();
            ProbeConnectTimeoutBox.Text = probe.ConnectTimeoutSeconds.ToString();
            ProbeStartupTimeoutBox.Text = probe.StartupTimeoutSeconds.ToString();
            ProbeFailureThresholdBox.Text = probe.FailureThreshold.ToString();
            ProbeEnabledBox.IsChecked = true;
        }
    }

    public CommandDefinition? Result { get; private set; }

    private void ProbeKind_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshProbeFields();
    private void ProbeEnabled_Changed(object sender, RoutedEventArgs e) => RefreshProbeFields();

    private void RefreshProbeFields()
    {
        if (ProbeFields is null || ProbeEnabledBox is null || ProbeTaskHint is null) return;
        var service = KindBox.SelectedIndex == 1;
        ProbeEnabledBox.IsEnabled = service;
        ProbeFields.IsEnabled = service && ProbeEnabledBox.IsChecked == true;
        ProbeTaskHint.Visibility = service ? Visibility.Collapsed : Visibility.Visible;
    }

    private TcpProbeConfiguration? ReadProbe(CommandKind kind)
    {
        if (kind != CommandKind.Service || ProbeEnabledBox.IsChecked != true) return null;
        static int Number(string value) => int.TryParse(value, out var number) ? number : -1;
        var probe = new TcpProbeConfiguration
        {
            Address = ProbeAddressBox.SelectedIndex == 1 ? "::1" : "127.0.0.1",
            Port = Number(ProbePortBox.Text), IntervalSeconds = Number(ProbeIntervalBox.Text),
            ConnectTimeoutSeconds = Number(ProbeConnectTimeoutBox.Text),
            StartupTimeoutSeconds = Number(ProbeStartupTimeoutBox.Text), FailureThreshold = Number(ProbeFailureThresholdBox.Text)
        };
        probe.Validate();
        return probe;
    }

    private void WorkingDirectoryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (CommandBox is not null) CommandBox.WorkingDirectory = WorkingDirectoryBox.Text;
    }

    private void CommandBox_SaveRequested(object? sender, EventArgs e) => SaveCommand();

    private void RefreshEnvironmentList(int selectedIndex = -1)
    {
        EnvironmentList.Items.Clear();
        foreach (var variable in _environmentVariables)
        {
            var status = !variable.IsSensitive ? "普通值（明文）" : variable.ValueState == "Required"
                ? "待填写" : IsSensitiveValueAvailable(variable) ? "已保存" : "不可解密";
            EnvironmentList.Items.Add($"{variable.Name}  ·  {status}");
        }
        if (selectedIndex >= 0 && selectedIndex < EnvironmentList.Items.Count) EnvironmentList.SelectedIndex = selectedIndex;
    }

    private static bool IsSensitiveValueAvailable(CommandEnvironmentVariable variable)
    {
        if (variable.ValueState != "Protected" || string.IsNullOrEmpty(variable.ProtectedValue)) return false;
        try { _ = SensitiveValueProtector.Unprotect(variable.ProtectedValue); return true; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private void EnvironmentList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        EditEnvironmentButton.IsEnabled = EnvironmentList.SelectedIndex >= 0;
        DeleteEnvironmentButton.IsEnabled = EnvironmentList.SelectedIndex >= 0;
    }

    private void AddEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var editor = new EnvironmentVariableEditorWindow(null, _environmentVariables.Select(variable => variable.Name)) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        _environmentVariables.Add(editor.Result);
        RefreshEnvironmentList(_environmentVariables.Count - 1);
    }

    private void EditEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var index = EnvironmentList.SelectedIndex;
        if (index < 0) return;
        var editor = new EnvironmentVariableEditorWindow(_environmentVariables[index],
            _environmentVariables.Where((_, position) => position != index).Select(variable => variable.Name)) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        _environmentVariables[index] = editor.Result;
        RefreshEnvironmentList(index);
    }

    private void DeleteEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var index = EnvironmentList.SelectedIndex;
        if (index < 0) return;
        _environmentVariables.RemoveAt(index);
        RefreshEnvironmentList(Math.Min(index, _environmentVariables.Count - 1));
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择命令运行目录", Multiselect = false };
        if (Directory.Exists(WorkingDirectoryBox.Text)) picker.InitialDirectory = WorkingDirectoryBox.Text;
        if (picker.ShowDialog(this) == true) WorkingDirectoryBox.Text = picker.FolderName;
    }

    private void SaveCommand_Click(object sender, RoutedEventArgs e)
    {
        SaveCommand();
    }

    private void SaveCommand()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(CommandBox.Text) || string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text))
        {
            MessageBox.Show(this, "请填写名称、命令和运行目录。", "命令信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(WorkingDirectoryBox.Text.Trim()))
        {
            MessageBox.Show(this, "运行目录不存在，请选择已存在的文件夹。", "未找到运行目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kind = (KindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "Service" ? CommandKind.Service : CommandKind.Task;
        TcpProbeConfiguration? probe;
        try { probe = ReadProbe(kind); }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(this, exception.Message + "\n端口 1–65535；间隔和连接超时 1–60 秒；启动等待 1–3600 秒且不小于连接超时；失败阈值 1–100。", "探测配置无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = new CommandDefinition
        {
            Id = _commandId == Guid.Empty ? Guid.NewGuid() : _commandId,
            Name = NameBox.Text.Trim(),
            Purpose = PurposeBox.Text.Trim(),
            Command = CommandBox.Text.Trim(),
            WorkingDirectory = Path.GetFullPath(WorkingDirectoryBox.Text.Trim()),
            Kind = kind,
            TcpProbe = probe,
            EnvironmentVariables = _environmentVariables.Select(variable => variable.Clone()).ToList()
        };
        DialogResult = true;
    }
}
