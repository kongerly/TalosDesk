namespace TalosDesk.Core.Editing;

internal static class CommandEditingRules
{
    private static readonly IReadOnlyDictionary<char, char> Pairs = new Dictionary<char, char>
    {
        ['\''] = '\'',
        ['"'] = '"',
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}'
    };

    public static bool TryGetClosingCharacter(char openingCharacter, out char closingCharacter) =>
        Pairs.TryGetValue(openingCharacter, out closingCharacter);

    public static bool IsPair(char openingCharacter, char closingCharacter) =>
        Pairs.TryGetValue(openingCharacter, out var expected) && expected == closingCharacter;

    public static bool IsClosingCharacter(char character) => Pairs.Values.Contains(character);

    public static string GetNewLineIndentation(string lineTextBeforeCaret)
    {
        ArgumentNullException.ThrowIfNull(lineTextBeforeCaret);
        var indentLength = 0;
        while (indentLength < lineTextBeforeCaret.Length && char.IsWhiteSpace(lineTextBeforeCaret[indentLength])) indentLength++;

        var indentation = lineTextBeforeCaret[..indentLength].Replace("\t", "    ", StringComparison.Ordinal);
        var trimmed = lineTextBeforeCaret.TrimEnd();
        if (trimmed.EndsWith('{') || trimmed.EndsWith('[') || trimmed.EndsWith('(')) indentation += "    ";
        return indentation;
    }

    public static bool IsIndentationPosition(string lineTextBeforeCaret) =>
        string.IsNullOrWhiteSpace(lineTextBeforeCaret);
}
