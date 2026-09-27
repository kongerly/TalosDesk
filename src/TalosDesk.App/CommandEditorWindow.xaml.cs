using System.IO;
using System.Windows;
using Microsoft.Win32;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

public partial class CommandEditorWindow : Window
{
    private readonly Guid _commandId;

    public CommandEditorWindow(string projectDirectory, CommandDefinition? existing = null)
    {
        InitializeComponent();
        WorkingDirectoryBox.Text = existing?.WorkingDirectory ?? projectDirectory;
        if (existing is null) return;

        _commandId = existing.Id;
        HeadingText.Text = "编辑命令";
        NameBox.Text = existing.Name;
        PurposeBox.Text = existing.Purpose;
        CommandBox.Text = existing.Command;
        KindBox.SelectedIndex = existing.Kind == CommandKind.Service ? 1 : 0;
    }

    public CommandDefinition? Result { get; private set; }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择命令运行目录", Multiselect = false };
        if (Directory.Exists(WorkingDirectoryBox.Text)) picker.InitialDirectory = WorkingDirectoryBox.Text;
        if (picker.ShowDialog(this) == true) WorkingDirectoryBox.Text = picker.FolderName;
    }

    private void SaveCommand_Click(object sender, RoutedEventArgs e)
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
        Result = new CommandDefinition
        {
            Id = _commandId == Guid.Empty ? Guid.NewGuid() : _commandId,
            Name = NameBox.Text.Trim(),
            Purpose = PurposeBox.Text.Trim(),
            Command = CommandBox.Text.Trim(),
            WorkingDirectory = Path.GetFullPath(WorkingDirectoryBox.Text.Trim()),
            Kind = kind
        };
        DialogResult = true;
    }
}
