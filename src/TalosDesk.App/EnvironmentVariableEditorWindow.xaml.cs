using System.Windows;
using System.Windows.Controls;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

public partial class EnvironmentVariableEditorWindow : Window
{
    private readonly CommandEnvironmentVariable? _original;
    private readonly HashSet<string> _otherNames;

    public EnvironmentVariableEditorWindow(CommandEnvironmentVariable? original, IEnumerable<string> otherNames)
    {
        InitializeComponent();
        _original = original?.Clone();
        _otherNames = new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase);
        if (_original is not null)
        {
            HeadingText.Text = "编辑环境变量";
            NameBox.Text = _original.Name;
            if (_original.IsSensitive)
            {
                SensitiveBox.IsChecked = true;
                StatusText.Text = _original.ValueState == "Required" ? "状态：待填写" : IsAvailable(_original) ? "状态：已保存" : "状态：不可解密，请替换值";
            }
            else
            {
                PlainValueBox.Text = _original.Value ?? string.Empty;
                StatusText.Text = "状态：普通值（明文）";
            }
        }
        UpdateMode();
    }

    public CommandEnvironmentVariable? Result { get; private set; }

    private static bool IsAvailable(CommandEnvironmentVariable variable)
    {
        if (variable.ValueState != "Protected" || string.IsNullOrEmpty(variable.ProtectedValue)) return false;
        try { _ = SensitiveValueProtector.Unprotect(variable.ProtectedValue); return true; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private void SensitiveBox_Changed(object sender, RoutedEventArgs e) => UpdateMode();

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRenameHint();

    private void ActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SecretValuePanel is null) return;
        SecretValuePanel.Visibility = SelectedAction() == EnvironmentValueEditAction.Replace ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMode()
    {
        if (PlainPanel is null || SecretPanel is null) return;
        var sensitive = SensitiveBox.IsChecked == true;
        PlainPanel.Visibility = sensitive ? Visibility.Collapsed : Visibility.Visible;
        SecretPanel.Visibility = sensitive ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPlainBox.Visibility = !sensitive && _original?.IsSensitive == true ? Visibility.Visible : Visibility.Collapsed;
        if (!sensitive)
        {
            if (_original?.IsSensitive == true) StatusText.Text = "原敏感值不会解密回填；请填写新的普通值。";
            else StatusText.Text = "状态：普通值（明文）";
            return;
        }

        StatusText.Text = _original?.IsSensitive == true
            ? _original.ValueState == "Required" ? "状态：待填写" : IsAvailable(_original) ? "状态：已保存" : "状态：不可解密，请替换值"
            : "请选择如何设置敏感值。";

        ActionBox.Items.Clear();
        if (_original?.IsSensitive == true)
            AddAction("保持原值", EnvironmentValueEditAction.KeepExisting);
        else if (_original is not null)
            AddAction("加密当前普通值", EnvironmentValueEditAction.EncryptExistingPlain);
        AddAction("替换值", EnvironmentValueEditAction.Replace);
        AddAction("设为待填写", EnvironmentValueEditAction.Require);
        ActionBox.SelectedIndex = _original?.IsSensitive == true ? 0 : -1;
        UpdateRenameHint();
    }

    private void AddAction(string label, EnvironmentValueEditAction action) =>
        ActionBox.Items.Add(new ComboBoxItem { Content = label, Tag = action });

    private EnvironmentValueEditAction? SelectedAction() =>
        (ActionBox.SelectedItem as ComboBoxItem)?.Tag is EnvironmentValueEditAction action ? action : null;

    private void UpdateRenameHint()
    {
        if (RenameHintText is null || SensitiveBox?.IsChecked != true) return;
        RenameHintText.Text = _original?.IsSensitive == true && NameBox.Text != _original.Name
            ? "改名后若选择“保持原值”，新变量会变为待填写；若要继续使用，请选择“替换值”并重新输入。"
            : "替换值可以是显式空字符串；“待填写”会阻止命令运行，直到重新录入。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text;
        if (_otherNames.Contains(name))
        {
            MessageBox.Show(this, "此命令已有同名环境变量（名称不区分大小写）。", "变量名重复", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sensitive = SensitiveBox.IsChecked == true;
        if (!sensitive && _original?.IsSensitive == true && ConfirmPlainBox.IsChecked != true)
        {
            MessageBox.Show(this, "请确认使用重新填写的普通值。原敏感值不会解密回填。", "需要确认明文值", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (sensitive && SelectedAction() is null)
        {
            MessageBox.Show(this, "请选择敏感变量的处理方式。", "处理方式未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Result = CommandEnvironmentVariableEditor.Apply(_original, name, sensitive,
                sensitive ? SelectedAction()!.Value : EnvironmentValueEditAction.Replace,
                sensitive ? SecretValueBox.Password : PlainValueBox.Text);
            SecretValueBox.Clear();
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "环境变量无效", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MessageBox.Show(this, "无法保护敏感值，请检查当前 Windows 用户环境后重试。", "保存敏感值失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
