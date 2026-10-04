using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TalosDesk.App;

internal partial class AppMessageDialogWindow : Window
{
    private readonly Button _initialButton;
    private readonly MessageBoxResult _dismissResult;

    internal AppMessageDialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        string? primaryText = null, string? secondaryText = null, string? cancelText = null)
    {
        InitializeComponent();
        Title = "TalosDesk · " + title;
        HeadingText.Text = title;
        MessageBox.Text = message;
        PrimaryActionButton.Tag = MessageBoxResult.OK;
        CancelActionButton.Visibility = NegativeActionButton.Visibility = Visibility.Collapsed;
        switch (buttons)
        {
            case MessageBoxButton.OK:
                PrimaryActionButton.Content = primaryText ?? "确定";
                _initialButton = PrimaryActionButton;
                _dismissResult = MessageBoxResult.OK;
                break;
            case MessageBoxButton.YesNo:
                PrimaryActionButton.Content = primaryText ?? "继续";
                PrimaryActionButton.Tag = MessageBoxResult.Yes;
                CancelActionButton.Content = secondaryText ?? "取消";
                CancelActionButton.Tag = MessageBoxResult.No;
                CancelActionButton.Visibility = Visibility.Visible;
                _initialButton = CancelActionButton;
                _dismissResult = MessageBoxResult.No;
                break;
            case MessageBoxButton.YesNoCancel:
                PrimaryActionButton.Content = primaryText ?? "替换";
                PrimaryActionButton.Tag = MessageBoxResult.Yes;
                NegativeActionButton.Content = secondaryText ?? "保留本机";
                NegativeActionButton.Tag = MessageBoxResult.No;
                NegativeActionButton.Visibility = Visibility.Visible;
                CancelActionButton.Content = cancelText ?? "取消导入";
                CancelActionButton.Tag = MessageBoxResult.Cancel;
                CancelActionButton.Visibility = Visibility.Visible;
                _initialButton = CancelActionButton;
                _dismissResult = MessageBoxResult.Cancel;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(buttons));
        }
        Result = _dismissResult;
        SetStatus(image);
        WindowPlacementController.AttachDialog(this);
        ContentRendered += (_, _) => _initialButton.Focus();
    }

    internal MessageBoxResult Result { get; private set; }

    private void SetStatus(MessageBoxImage image)
    {
        var (category, brushKey, background, geometry) = image switch
        {
            MessageBoxImage.Error => ("错误", "ErrorBrush", "#F8ECE8", "M12,1 A11,11 0 1 1 11.99,1 M8,8 L16,16 M16,8 L8,16"),
            MessageBoxImage.Warning => ("警告", "GoldBrush", "#FBF0E7", "M12,2 L23,22 L1,22 Z M12,8 L12,14 M12,18 L12,18.5"),
            MessageBoxImage.Question => ("请确认", "JadeBrush", "#E5F3F8", "M12,1 A11,11 0 1 1 11.99,1 M8,8 C8,3 17,3 17,8 C17,11 12,10 12,14 M12,18 L12,18.5"),
            _ => ("提示", "JadeBrush", "#E5F3F8", "M12,1 A11,11 0 1 1 11.99,1 M12,10 L12,18 M12,6 L12,6.5")
        };
        var brush = (Brush)FindResource(brushKey);
        CategoryText.Text = category;
        CategoryText.Foreground = StatusIcon.Stroke = brush;
        StatusIcon.Data = Geometry.Parse(geometry);
        StatusBadge.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        Result = (MessageBoxResult)((Button)sender).Tag;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = _dismissResult;
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && (Keyboard.FocusedElement is Button || _dismissResult == MessageBoxResult.OK))
        {
            e.Handled = true;
            Action_Click(Keyboard.FocusedElement as Button ?? PrimaryActionButton, new RoutedEventArgs());
        }
    }
}
