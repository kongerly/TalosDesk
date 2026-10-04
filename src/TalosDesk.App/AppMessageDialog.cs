using System.Windows;
using System.Windows.Input;

namespace TalosDesk.App;

internal static class AppMessageDialog
{
    internal static MessageBoxResult Show(string message, string title, MessageBoxButton buttons,
        MessageBoxImage image, string? primaryText = null, string? secondaryText = null, string? cancelText = null) =>
        Show(null, message, title, buttons, image, primaryText, secondaryText, cancelText);

    internal static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage image, string? primaryText = null, string? secondaryText = null, string? cancelText = null)
    {
        var previousFocus = Keyboard.FocusedElement;
        var dialog = new AppMessageDialogWindow(message, title, buttons, image, primaryText, secondaryText, cancelText)
        {
            Owner = owner,
            ShowInTaskbar = owner is null
        };
        try
        {
            dialog.ShowDialog();
            return dialog.Result;
        }
        finally
        {
            if (owner is { IsVisible: true, IsEnabled: true })
            {
                owner.Activate();
                if (previousFocus is UIElement { IsVisible: true, IsEnabled: true } element &&
                    Window.GetWindow(element) == owner)
                    element.Focus();
            }
        }
    }
}
