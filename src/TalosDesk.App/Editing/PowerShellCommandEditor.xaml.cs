using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using TalosDesk.Core.Editing;

namespace TalosDesk.App.Editing;

public partial class PowerShellCommandEditor : UserControl
{
    private readonly CommandCompletionProvider _completionProvider = new();
    private CancellationTokenSource? _completionCancellation;
    private bool _applyingCompletion;
    private string _workingDirectory = string.Empty;

    public PowerShellCommandEditor()
    {
        InitializeComponent();
        ConfigureEditor();
        LoadSyntaxHighlighting();
        UpdateStatus();
    }

    public event EventHandler? SaveRequested;

    public string Text
    {
        get => Editor.Text;
        set
        {
            Editor.Text = value ?? string.Empty;
            Editor.CaretOffset = Editor.Text.Length;
        }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            _workingDirectory = value ?? string.Empty;
            CancelCompletion();
            UpdateStatus();
        }
    }

    public void FocusEditor() => Editor.Focus();

    private void ConfigureEditor()
    {
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.TextArea.LeftMargins.Insert(0, new TerminalPromptMargin(Editor.TextArea.TextView));
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            CancelCompletion();
            UpdateStatus();
        };
        Editor.TextArea.TextEntering += Editor_TextEntering;
    }

    private void LoadSyntaxHighlighting()
    {
        var resource = Application.GetResourceStream(
            new Uri("pack://application:,,,/TalosDesk.App;component/Editing/PowerShellHighlighting.xshd"));
        if (resource is null) return;

        using var stream = resource.Stream;
        using var reader = XmlReader.Create(stream);
        try
        {
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception exception) when (exception is HighlightingDefinitionInvalidException or XmlException or IOException)
        {
            // Syntax color is optional editing assistance; a malformed resource must not prevent command editing.
        }
    }

    private void Editor_TextChanged(object sender, EventArgs e)
    {
        if (!_applyingCompletion) CancelCompletion();
        UpdateStatus();
    }

    private void Editor_TextEntering(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;
        CancelCompletion();
        var character = e.Text[0];

        if (Editor.SelectionLength == 0 && Editor.CaretOffset < Editor.Document.TextLength &&
            Editor.Document.GetCharAt(Editor.CaretOffset) == character && CommandEditingRules.IsClosingCharacter(character))
        {
            Editor.CaretOffset++;
            e.Handled = true;
            return;
        }

        if (CommandEditingRules.TryGetClosingCharacter(character, out var closingCharacter))
        {
            using (Editor.Document.RunUpdate())
            {
                if (Editor.SelectionLength > 0)
                {
                    var selectionStart = Editor.SelectionStart;
                    var selectedText = Editor.SelectedText;
                    Editor.Document.Replace(selectionStart, Editor.SelectionLength, $"{character}{selectedText}{closingCharacter}");
                    Editor.Select(selectionStart + 1, selectedText.Length);
                }
                else
                {
                    var offset = Editor.CaretOffset;
                    Editor.Document.Insert(offset, $"{character}{closingCharacter}");
                    Editor.CaretOffset = offset + 1;
                }
            }

            e.Handled = true;
            return;
        }

    }

    private async void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CancelCompletion();
            SaveRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (CompletionPopup.IsOpen)
        {
            if (e.Key is Key.Tab or Key.Down)
            {
                MoveCompletionSelection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up)
            {
                MoveCompletionSelection(-1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                ApplySelectedCompletion();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                CancelCompletion();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await ShowCompletionAsync(insertIndentWhenEmpty: false);
            return;
        }

        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await ShowCompletionAsync(insertIndentWhenEmpty: true);
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            InsertNewLineWithIndentation();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && TryDeleteEmptyPair())
        {
            e.Handled = true;
        }
    }

    private async Task ShowCompletionAsync(bool insertIndentWhenEmpty)
    {
        CancelCompletion();
        var requestText = Editor.Text;
        var requestCaret = Editor.CaretOffset;
        var cancellation = new CancellationTokenSource();
        _completionCancellation = cancellation;
        StatusText.Text = "PowerShell 7 · 正在查找安全补全…";

        try
        {
            var completions = await _completionProvider.GetCompletionsAsync(
                requestText, requestCaret, WorkingDirectory.Trim(), cancellation.Token);
            if (cancellation.IsCancellationRequested || requestText != Editor.Text || requestCaret != Editor.CaretOffset) return;

            if (completions.Count == 0)
            {
                if (insertIndentWhenEmpty && IsCaretInIndentation()) Editor.Document.Insert(Editor.CaretOffset, "    ");
                return;
            }

            CompletionList.ItemsSource = completions.Select(CompletionViewItem.FromCompletion).ToArray();
            CompletionList.SelectedIndex = 0;
            PositionCompletionPopup();
            CompletionPopup.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            // A new caret position or request superseded this completion.
        }
        finally
        {
            if (ReferenceEquals(_completionCancellation, cancellation)) _completionCancellation = null;
            cancellation.Dispose();
            UpdateStatus();
        }
    }

    private void PositionCompletionPopup()
    {
        var caret = Editor.TextArea.Caret.CalculateCaretRectangle();
        CompletionPopup.HorizontalOffset = Math.Max(0, caret.Left + 42 - Editor.TextArea.TextView.HorizontalOffset);
        CompletionPopup.VerticalOffset = Math.Max(0, caret.Bottom + 4);
    }

    private void MoveCompletionSelection(int direction)
    {
        if (CompletionList.Items.Count == 0) return;
        CompletionList.SelectedIndex = (CompletionList.SelectedIndex + direction + CompletionList.Items.Count) % CompletionList.Items.Count;
        CompletionList.ScrollIntoView(CompletionList.SelectedItem);
    }

    private void ApplySelectedCompletion()
    {
        if (CompletionList.SelectedItem is not CompletionViewItem selected) return;
        _applyingCompletion = true;
        try
        {
            Editor.Document.Replace(selected.ReplacementStart, selected.ReplacementLength, selected.InsertText);
            Editor.CaretOffset = selected.ReplacementStart + selected.InsertText.Length;
        }
        finally
        {
            _applyingCompletion = false;
            CancelCompletion();
            Editor.Focus();
        }
    }

    private void CompletionList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ApplySelectedCompletion();

    private void InsertNewLineWithIndentation()
    {
        CancelCompletion();
        var insertionStart = Editor.SelectionStart;
        var line = Editor.Document.GetLineByOffset(insertionStart);
        var beforeCaret = Editor.Document.GetText(line.Offset, insertionStart - line.Offset);
        var insertion = Environment.NewLine + CommandEditingRules.GetNewLineIndentation(beforeCaret);
        Editor.Document.Replace(insertionStart, Editor.SelectionLength, insertion);
        Editor.CaretOffset = insertionStart + insertion.Length;
    }

    private bool IsCaretInIndentation()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        var beforeCaret = Editor.Document.GetText(line.Offset, Editor.CaretOffset - line.Offset);
        return CommandEditingRules.IsIndentationPosition(beforeCaret);
    }

    private bool TryDeleteEmptyPair()
    {
        if (Editor.SelectionLength != 0 || Editor.CaretOffset == 0 || Editor.CaretOffset >= Editor.Document.TextLength) return false;
        var opening = Editor.Document.GetCharAt(Editor.CaretOffset - 1);
        var closing = Editor.Document.GetCharAt(Editor.CaretOffset);
        if (!CommandEditingRules.IsPair(opening, closing)) return false;

        Editor.Document.Remove(Editor.CaretOffset - 1, 2);
        Editor.CaretOffset--;
        return true;
    }

    private void CancelCompletion()
    {
        CompletionPopup.IsOpen = false;
        _completionCancellation?.Cancel();
        _completionCancellation = null;
    }

    private void UpdateStatus()
    {
        if (!IsInitialized) return;
        var location = Editor.Document.GetLocation(Editor.CaretOffset);
        var directoryStatus = Directory.Exists(WorkingDirectory.Trim())
            ? string.Empty
            : " · 运行目录无效，路径补全不可用";
        StatusText.Text = $"PowerShell 7 · 安全补全 · 第 {location.Line} 行，第 {location.Column} 列{directoryStatus}";
    }

    private sealed record CompletionViewItem(
        string DisplayText,
        string InsertText,
        int ReplacementStart,
        int ReplacementLength,
        string KindText)
    {
        public static CompletionViewItem FromCompletion(CommandCompletionItem completion) => new(
            completion.DisplayText,
            completion.InsertText,
            completion.ReplacementStart,
            completion.ReplacementLength,
            completion.Kind switch
            {
                CommandCompletionKind.Command => "命令",
                CommandCompletionKind.Directory => "目录",
                CommandCompletionKind.File => "文件",
                CommandCompletionKind.EnvironmentVariable => "环境变量",
                _ => string.Empty
            });
    }

    private sealed class TerminalPromptMargin : AbstractMargin
    {
        private readonly TextView _textView;
        private readonly Typeface _typeface = new(new FontFamily("Cascadia Code, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private readonly Brush _foreground = new SolidColorBrush(Color.FromRgb(44, 120, 152));

        public TerminalPromptMargin(TextView textView)
        {
            _textView = textView;
            _textView.VisualLinesChanged += (_, _) => InvalidateVisual();
            _textView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) => new(42, 0);

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(237, 247, 251)), null,
                new Rect(0, 0, RenderSize.Width, RenderSize.Height));
            if (!_textView.VisualLinesValid) return;

            foreach (var line in _textView.VisualLines)
            {
                var text = line.FirstDocumentLine.LineNumber == 1 ? "PS>" : ">>";
                var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    _typeface, 11, _foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var y = line.VisualTop - _textView.VerticalOffset + Math.Max(0, (_textView.DefaultLineHeight - formatted.Height) / 2);
                drawingContext.DrawText(formatted, new Point(RenderSize.Width - formatted.Width - 6, y));
            }
        }
    }
}
