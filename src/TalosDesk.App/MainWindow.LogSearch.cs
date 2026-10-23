using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TalosDesk.Core.Processes;

namespace TalosDesk.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _logSearchTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
    private CancellationTokenSource? _logSearchCancellation;
    private LogSearchAdorner? _logSearchAdorner;
    private int[] _logMatches = [];
    private int _logMatchIndex = -1;
    private long _logSearchVersion;

    private void InitializeLogSearch()
    {
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => OpenLogSearch()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        LogSearchBox.TextChanged += (_, _) => ResetLogSearchScope();
        OutputTextBox.TextChanged += (_, _) => ResetLogSearchScope();
        OutputTextBox.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler((_, _) => _logSearchAdorner?.InvalidateVisual()));
        OutputTextBox.SizeChanged += (_, _) => _logSearchAdorner?.InvalidateVisual();
        _logSearchTimer.Tick += SearchDisplayedLog;
        Closed += (_, _) => { _logSearchTimer.Stop(); _logSearchCancellation?.Cancel(); };
    }

    private void FindOutput_Click(object sender, RoutedEventArgs e) => OpenLogSearch();

    private void OpenLogSearch()
    {
        SetPage(MainPage.Output);
        LogSearchPanel.Visibility = Visibility.Visible;
        if (_logSearchAdorner is null)
        {
            _logSearchAdorner = new LogSearchAdorner(OutputTextBox);
            OutputAdornerHost.AdornerLayer.Add(_logSearchAdorner);
        }
        LogSearchBox.Focus();
        LogSearchBox.SelectAll();
        ResetLogSearchScope();
    }

    private void CloseLogSearch_Click(object sender, RoutedEventArgs e) => CloseLogSearch();

    private void CloseLogSearch()
    {
        LogSearchPanel.Visibility = Visibility.Collapsed;
        _logSearchTimer.Stop();
        _logSearchCancellation?.Cancel();
        _logSearchVersion++;
        _logMatches = [];
        _logMatchIndex = -1;
        UpdateLogMatches();
        OutputTextBox.Focus();
    }

    private void LogSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseLogSearch(); e.Handled = true; }
        if (e.Key == Key.Enter) { MoveLogMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
    }

    private void ResetLogSearchScope()
    {
        _logMatchIndex = -1;
        _logMatches = [];
        _logSearchCancellation?.Cancel();
        if (LogMatchCountText is not null) UpdateLogMatches();
        ScheduleLogSearch();
    }

    private void ScheduleLogSearch()
    {
        _logSearchVersion++;
        if (LogSearchPanel?.Visibility != Visibility.Visible || _windowClosed) return;
        var channel = HistoryStreamComboBox.SelectedIndex == 1 ? "诊断输出" : "标准输出";
        LogSearchScopeText.Text = $"范围：{(SelectedHistoricalRun is null ? "当前显示" : "所选历史批次")} · {channel} · 最近 10,000 行；按字面匹配，忽略大小写。";
        // 追加只合并请求，不反复推迟计时器；关键词与范围变化立即清除旧结果。
        _logSearchTimer.Start();
    }

    private async void SearchDisplayedLog(object? sender, EventArgs e)
    {
        _logSearchTimer.Stop();
        _logSearchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _logSearchCancellation = cancellation;
        var version = _logSearchVersion;
        var text = OutputTextBox.Text;
        var keyword = LogSearchBox.Text;
        var prefix = SelectedHistoricalRun is null ? (HistoryStreamComboBox.SelectedIndex == 1 ? "诊断输出  " : "标准输出  ") : null;
        var previous = _logMatchIndex >= 0 && _logMatchIndex < _logMatches.Length ? _logMatches[_logMatchIndex] : -1;
        try
        {
            var matches = await Task.Run(() => LogSearch.FindAll(text, keyword, prefix, cancellation.Token), cancellation.Token);
            if (_windowClosed || cancellation.IsCancellationRequested || version != _logSearchVersion) return;
            _logMatches = matches;
            _logMatchIndex = previous < 0 ? -1 : Array.BinarySearch(matches, previous);
            if (_logMatchIndex < 0) _logMatchIndex = -1;
            UpdateLogMatches();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_logSearchCancellation, cancellation)) _logSearchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void PreviousLogMatch_Click(object sender, RoutedEventArgs e) => MoveLogMatch(-1);
    private void NextLogMatch_Click(object sender, RoutedEventArgs e) => MoveLogMatch(1);

    private void MoveLogMatch(int direction)
    {
        if (_logMatches.Length == 0) return;
        _logMatchIndex = _logMatchIndex < 0 ? (direction > 0 ? 0 : _logMatches.Length - 1) :
            (_logMatchIndex + direction + _logMatches.Length) % _logMatches.Length;
        OutputTextBox.Select(_logMatches[_logMatchIndex], LogSearchBox.Text.Length);
        OutputTextBox.ScrollToLine(OutputTextBox.GetLineIndexFromCharacterIndex(_logMatches[_logMatchIndex]));
        UpdateLogMatches();
    }

    private void UpdateLogMatches()
    {
        LogMatchCountText.Text = $"{_logMatchIndex + 1} / {_logMatches.Length}";
        PreviousLogMatchButton.IsEnabled = NextLogMatchButton.IsEnabled = _logMatches.Length > 0;
        _logSearchAdorner?.SetMatches(_logMatches, LogSearchBox.Text.Length);
    }
}
