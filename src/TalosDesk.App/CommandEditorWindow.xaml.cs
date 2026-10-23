using System.Globalization;
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
        ShellBox.SelectedIndex = existing.Shell == CommandShell.Cmd ? 1 : 0;
        RefreshShell();
        AutoRestartEnabledBox.IsChecked = existing.AutoRestartEnabled;
        if (existing.TcpProbe is { } probe)
        {
            ProbeAddressBox.SelectedIndex = probe.Address == "::1" ? 1 : 0;
            ProbePortBox.Text = probe.Port.ToString(CultureInfo.InvariantCulture);
            ProbeIntervalBox.Text = probe.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
            ProbeConnectTimeoutBox.Text = probe.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ProbeStartupTimeoutBox.Text = probe.StartupTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ProbeFailureThresholdBox.Text = probe.FailureThreshold.ToString(CultureInfo.InvariantCulture);
            ProbeEnabledBox.IsChecked = true;
        }
    }

    public CommandDefinition? Result { get; private set; }

    private void ShellBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshShell();

    private void RefreshShell()
    {
        if (CommandBox is null || ShellHintText is null) return;
        CommandBox.Shell = ShellBox.SelectedIndex == 1 ? CommandShell.Cmd : CommandShell.PowerShell;
        ShellHintText.Text = CommandBox.Shell == CommandShell.Cmd
            ? "Cmd 非交互单行，最多 7,000 字符，禁用 AutoRun 和延迟变量展开；UTF-8 输出。含空格路径用双引号，变量用 %NAME%；批处理可用 call \"脚本.cmd\"。不提供 PowerShell 补全，旧编码工具需自行配置 UTF-8。命令以明文保存和导出，不要写入密钥。"
            : "命令以明文保存和导出，不要写入密钥。PowerShell 7 非交互运行，不加载个人配置文件；目录中的程序可用 .\\程序名.exe。";
    }

    private void ProbeKind_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshProbeFields();
    private void ProbeEnabled_Changed(object sender, RoutedEventArgs e) => RefreshProbeFields();

    private void RefreshProbeFields()
    {
        if (AutoRestartEnabledBox is null || ProbeFields is null || ProbeEnabledBox is null || ProbeTaskHint is null) return;
        var service = KindBox.SelectedIndex == 1;
        ProbeEnabledBox.IsEnabled = service;
        AutoRestartEnabledBox.IsEnabled = service;
        if (!service) AutoRestartEnabledBox.IsChecked = false;
        ProbeFields.IsEnabled = service && ProbeEnabledBox.IsChecked == true;
        ProbeTaskHint.Visibility = service ? Visibility.Collapsed : Visibility.Visible;
    }

    private TcpProbeConfiguration? ReadProbe(CommandKind kind)
    {
        if (kind != CommandKind.Service || ProbeEnabledBox.IsChecked != true) return null;
        static int Number(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : -1;
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
            AppMessageDialog.Show(this, "请填写名称、命令和运行目录。", "命令信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(WorkingDirectoryBox.Text.Trim()))
        {
            AppMessageDialog.Show(this, "运行目录不存在，请选择已存在的文件夹。", "未找到运行目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kind = (KindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "Service" ? CommandKind.Service : CommandKind.Task;
        var shell = ShellBox.SelectedIndex == 1 ? CommandShell.Cmd : CommandShell.PowerShell;
        try { CommandShellRules.Validate(shell, CommandBox.Text); }
        catch (InvalidDataException exception)
        {
            AppMessageDialog.Show(this, exception.Message, "命令配置无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TcpProbeConfiguration? probe;
        try { probe = ReadProbe(kind); }
        catch (InvalidDataException exception)
        {
            AppMessageDialog.Show(this, exception.Message + "\n端口 1–65535；间隔和连接超时 1–60 秒；启动等待 1–3600 秒且不小于连接超时；失败阈值 1–100。", "探测配置无效", MessageBoxButton.OK, MessageBoxImage.Information);
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
            Shell = shell,
            TcpProbe = probe,
            AutoRestartEnabled = kind == CommandKind.Service && AutoRestartEnabledBox.IsChecked == true,
            EnvironmentVariables = _environmentVariables.Select(variable => variable.Clone()).ToList()
        };
        DialogResult = true;
    }
}
