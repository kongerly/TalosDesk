using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TalosDesk.App;

internal sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    public TextPromptWindow(string title, string label, string initialValue)
    {
        Title = title;
        Width = 420;
        Height = 205;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(232, 237, 242));
        Foreground = new SolidColorBrush(Color.FromRgb(24, 44, 57));
        FontFamily = new FontFamily("Segoe UI");

        var layout = new Grid { Margin = new Thickness(22) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) });
        var labelBlock = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
        Grid.SetRow(labelBlock, 1);
        layout.Children.Add(labelBlock);
        _input = new TextBox { Text = initialValue, Padding = new Thickness(9, 7, 9, 7), FontSize = 13, BorderBrush = new SolidColorBrush(Color.FromRgb(201, 211, 220)) };
        Grid.SetRow(_input, 2);
        layout.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Add project", IsDefault = true, Padding = new Thickness(13, 7, 13, 7), Background = new SolidColorBrush(Color.FromRgb(180, 90, 60)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(180, 90, 60)), FontWeight = FontWeights.SemiBold };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 4);
        layout.Children.Add(buttons);
        Content = layout;
        Loaded += (_, _) => { _input.SelectAll(); _input.Focus(); };
    }

    public string Value => _input.Text;
}
