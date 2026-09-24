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
        HeadingText.Text = "Edit command";
        NameBox.Text = existing.Name;
        PurposeBox.Text = existing.Purpose;
        CommandBox.Text = existing.Command;
        KindBox.SelectedIndex = existing.Kind == CommandKind.Service ? 1 : 0;
    }

    public CommandDefinition? Result { get; private set; }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the command working folder", Multiselect = false };
        if (Directory.Exists(WorkingDirectoryBox.Text)) picker.InitialDirectory = WorkingDirectoryBox.Text;
        if (picker.ShowDialog(this) == true) WorkingDirectoryBox.Text = picker.FolderName;
    }

    private void SaveCommand_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(CommandBox.Text) || string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text))
        {
            MessageBox.Show(this, "Enter a name, command, and working folder.", "Command details are incomplete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(WorkingDirectoryBox.Text.Trim()))
        {
            MessageBox.Show(this, "The working folder does not exist. Choose an existing folder.", "Working folder not found", MessageBoxButton.OK, MessageBoxImage.Information);
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
