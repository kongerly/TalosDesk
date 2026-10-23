using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TalosDesk.App;

internal sealed class LogSearchAdorner(TextBox textBox) : Adorner(textBox)
{
    private int[] _matches = [];
    private int _length;
    private static readonly Brush Highlight = new SolidColorBrush(Color.FromArgb(95, 240, 177, 45));

    internal void SetMatches(int[] matches, int length)
    {
        _matches = matches;
        _length = length;
        IsHitTestVisible = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_length == 0 || _matches.Length == 0 || textBox.LineCount == 0) return;
        var firstLine = textBox.GetFirstVisibleLineIndex();
        var lastLine = textBox.GetLastVisibleLineIndex();
        if (firstLine < 0 || lastLine < firstLine) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(textBox.RenderSize)));
        for (var line = firstLine; line <= lastLine; line++)
        {
            var lineStart = textBox.GetCharacterIndexFromLineIndex(line);
            var bounds = textBox.GetRectFromCharacterIndex(lineStart);
            if (bounds.IsEmpty) continue;
            var y = bounds.Top + bounds.Height / 2;
            var first = textBox.GetCharacterIndexFromPoint(new Point(1, y), true);
            var last = textBox.GetCharacterIndexFromPoint(new Point(Math.Max(1, textBox.ActualWidth - 1), y), true);
            if (first < 0 || last < first) continue;
            var index = Array.BinarySearch(_matches, first);
            if (index < 0) index = ~index;
            if (index > 0 && _matches[index - 1] + _length > first) index--;
            for (; index < _matches.Length && _matches[index] <= last; index++)
            {
                var start = _matches[index];
                if (start + _length > textBox.Text.Length) continue;
                var left = textBox.GetRectFromCharacterIndex(start);
                var right = textBox.GetRectFromCharacterIndex(start + _length - 1, true);
                if (left.IsEmpty || right.IsEmpty) continue;
                drawingContext.DrawRectangle(Highlight, null, new Rect(left.TopLeft, new Point(Math.Max(left.Left + 1, right.Right), left.Bottom)));
            }
        }
        drawingContext.Pop();
    }
}
