using System.Windows;

namespace TalosDesk.App;

public partial class WorkspaceImportPreviewWindow : Window
{
    public WorkspaceImportPreviewWindow(string preview)
    {
        InitializeComponent();
        WindowPlacementController.Attach(this);
        PreviewBox.Text = preview;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
