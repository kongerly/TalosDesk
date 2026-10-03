using System.IO;
using System.Windows;
using Microsoft.Win32;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

public partial class ProjectEditorWindow : Window
{
    private readonly Guid _projectId;

    public ProjectEditorWindow(ProjectDefinition? existing = null)
    {
        InitializeComponent();
        WindowPlacementController.Attach(this);
        if (existing is null) return;

        _projectId = existing.Id;
        HeadingText.Text = "编辑项目";
        SaveButton.Content = "保存修改";
        NameBox.Text = existing.Name;
        DirectoryBox.Text = existing.Directory;
    }

    public ProjectDefinition? Result { get; private set; }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择本地项目文件夹", Multiselect = false };
        if (Directory.Exists(DirectoryBox.Text)) picker.InitialDirectory = DirectoryBox.Text;
        if (picker.ShowDialog(this) == true)
        {
            DirectoryBox.Text = picker.FolderName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(picker.FolderName));
            }
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(DirectoryBox.Text))
        {
            MessageBox.Show(this, "请填写项目名称并选择项目文件夹。", "项目信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var directory = DirectoryBox.Text.Trim();
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(this, "项目文件夹不存在，请选择已存在的文件夹。", "未找到项目文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = new ProjectDefinition
        {
            Id = _projectId == Guid.Empty ? Guid.NewGuid() : _projectId,
            Name = NameBox.Text.Trim(),
            Directory = Path.GetFullPath(directory)
        };
        DialogResult = true;
    }
}
