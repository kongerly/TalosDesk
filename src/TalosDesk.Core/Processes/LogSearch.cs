namespace TalosDesk.Core.Processes;

/// <summary>Searches only a snapshot of the already redacted display text.</summary>
public static class LogSearch
{
    public static int[] FindAll(string text, string keyword, string? linePrefix = null,
        CancellationToken cancellationToken = default)
    {
        if (keyword.Length == 0) return [];
        var start = 0;
        var lines = 0;
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text[index] != '\n' && (text[index] != '\r' || index + 1 < text.Length && text[index + 1] == '\n')) continue;
            if (++lines == 10_000) { start = index + 1; break; }
        }
        var matches = new List<int>();
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = text.AsSpan(start).IndexOfAny('\r', '\n');
            end = end < 0 ? text.Length : start + end;
            if (linePrefix is null || text.AsSpan(start, end - start).StartsWith(linePrefix, StringComparison.Ordinal))
            {
                var cursor = start;
                while (cursor <= end - keyword.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var found = text.IndexOf(keyword, cursor, end - cursor, StringComparison.OrdinalIgnoreCase);
                    if (found < 0) break;
                    matches.Add(found);
                    cursor = found + keyword.Length;
                }
            }
            start = end + 1;
            if (end < text.Length && text[end] == '\r' && start < text.Length && text[start] == '\n') start++;
        }
        return matches.ToArray();
    }
}
