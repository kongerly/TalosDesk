using System.Collections;

namespace TalosDesk.Core.Editing;

internal enum CommandCompletionKind
{
    Command,
    Directory,
    File,
    EnvironmentVariable
}

internal sealed record CommandCompletionItem(
    string DisplayText,
    string InsertText,
    int ReplacementStart,
    int ReplacementLength,
    CommandCompletionKind Kind);

internal sealed class CommandCompletionProvider
{
    private const int MaximumResults = 100;
    private static readonly char[] StatementSeparators = ['|', ';', '&', '\r', '\n'];
    private readonly Func<string?> _pathReader;
    private readonly Func<string?> _pathExtensionsReader;
    private readonly Func<IDictionary> _environmentReader;

    public CommandCompletionProvider()
        : this(
            () => Environment.GetEnvironmentVariable("PATH"),
            () => Environment.GetEnvironmentVariable("PATHEXT"),
            Environment.GetEnvironmentVariables)
    {
    }

    internal CommandCompletionProvider(
        Func<string?> pathReader,
        Func<string?> pathExtensionsReader,
        Func<IDictionary> environmentReader)
    {
        _pathReader = pathReader;
        _pathExtensionsReader = pathExtensionsReader;
        _environmentReader = environmentReader;
    }

    public Task<IReadOnlyList<CommandCompletionItem>> GetCompletionsAsync(
        string text,
        int caretOffset,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (caretOffset < 0 || caretOffset > text.Length) throw new ArgumentOutOfRangeException(nameof(caretOffset));

        return Task.Run<IReadOnlyList<CommandCompletionItem>>(
            () => GetCompletions(text, caretOffset, workingDirectory, cancellationToken), cancellationToken);
    }

    internal IReadOnlyList<CommandCompletionItem> GetCompletions(
        string text,
        int caretOffset,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = CompletionToken.Parse(text, caretOffset);

        if (token.Value.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
        {
            return CompleteEnvironmentVariables(token, cancellationToken);
        }

        var results = new List<CommandCompletionItem>();
        if (token.IsCommandPosition && !token.Value.ContainsAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            results.AddRange(CompleteCommands(token, workingDirectory, cancellationToken));
        }

        if (ShouldCompletePath(token))
        {
            results.AddRange(CompletePaths(token, workingDirectory, cancellationToken));
        }

        return results
            .DistinctBy(item => (item.InsertText, item.Kind), CompletionItemKeyComparer.Instance)
            .OrderBy(item => GetKindOrder(item.Kind))
            .ThenBy(item => item.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumResults)
            .ToArray();
    }

    private List<CommandCompletionItem> CompleteEnvironmentVariables(
        CompletionToken token,
        CancellationToken cancellationToken)
    {
        const string prefixMarker = "$env:";
        var prefix = token.Value[prefixMarker.Length..];
        var replacementStart = token.ReplacementStart + prefixMarker.Length;
        var variables = new List<CommandCompletionItem>();

        foreach (DictionaryEntry entry in _environmentReader())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Key is not string name || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            variables.Add(new CommandCompletionItem(
                $"$env:{name}", name, replacementStart, prefix.Length, CommandCompletionKind.EnvironmentVariable));
        }

        return variables
            .DistinctBy(item => item.InsertText, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.InsertText, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumResults)
            .ToList();
    }

    private List<CommandCompletionItem> CompleteCommands(
        CompletionToken token,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (token.Value.StartsWith('$') || token.Value.StartsWith('-')) return [];

        var extensions = (_pathExtensionsReader() ?? ".COM;.EXE;.BAT;.CMD")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commands = new List<CommandCompletionItem>();
        var seenCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pathEntry in (_pathReader() ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory;
            try
            {
                var cleanedEntry = pathEntry.Trim('"');
                directory = Path.IsPathRooted(cleanedEntry)
                    ? cleanedEntry
                    : Path.GetFullPath(cleanedEntry, workingDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!Directory.Exists(directory)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extension = Path.GetExtension(file);
                    if (!extensions.Contains(extension)) continue;
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith(token.Value, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seenCommands.Add(name)) continue;
                    commands.Add(CreateItem(token, name, name, CommandCompletionKind.Command));
                    if (commands.Count >= MaximumResults) break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Completion is best effort. One inaccessible PATH entry must not hide the other entries.
            }

            if (commands.Count >= MaximumResults) break;
        }

        return commands;
    }

    private static List<CommandCompletionItem> CompletePaths(
        CompletionToken token,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workingDirectory)) return [];

        var value = token.Value;
        var lastSeparator = Math.Max(value.LastIndexOf(Path.DirectorySeparatorChar), value.LastIndexOf(Path.AltDirectorySeparatorChar));
        var typedDirectory = lastSeparator >= 0 ? value[..(lastSeparator + 1)] : string.Empty;
        var namePrefix = lastSeparator >= 0 ? value[(lastSeparator + 1)..] : value;
        string lookupDirectory;

        try
        {
            lookupDirectory = Path.GetFullPath(typedDirectory.Length == 0 ? "." : typedDirectory, workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        if (!Directory.Exists(lookupDirectory)) return [];
        var results = new List<CommandCompletionItem>();

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(lookupDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (!name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var isDirectory = Directory.Exists(entry);
                var unquotedInsertion = typedDirectory + name + (isDirectory ? Path.DirectorySeparatorChar : string.Empty);
                var insertion = token.IsQuoted || !unquotedInsertion.Contains(' ')
                    ? unquotedInsertion
                    : token.IsCommandPosition
                        ? $"& \"{unquotedInsertion}\""
                        : $"\"{unquotedInsertion}\"";
                results.Add(CreateItem(token, name + (isDirectory ? "\\" : string.Empty), insertion,
                    isDirectory ? CommandCompletionKind.Directory : CommandCompletionKind.File));
                if (results.Count >= MaximumResults) break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        return results;
    }

    private static bool ShouldCompletePath(CompletionToken token)
    {
        if (!token.IsCommandPosition) return !token.Value.StartsWith('-') && !token.Value.StartsWith('$');
        return token.Value.StartsWith('.') || token.Value.StartsWith('\\') || token.Value.StartsWith('/') ||
               Path.IsPathRooted(token.Value) || token.Value.ContainsAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static CommandCompletionItem CreateItem(
        CompletionToken token,
        string displayText,
        string insertText,
        CommandCompletionKind kind) =>
        new(displayText, insertText, token.ReplacementStart, token.ReplacementLength, kind);

    private static int GetKindOrder(CommandCompletionKind kind) => kind switch
    {
        CommandCompletionKind.Directory => 0,
        CommandCompletionKind.File => 1,
        CommandCompletionKind.Command => 2,
        CommandCompletionKind.EnvironmentVariable => 3,
        _ => 4
    };

    private sealed class CompletionItemKeyComparer : IEqualityComparer<(string InsertText, CommandCompletionKind Kind)>
    {
        public static CompletionItemKeyComparer Instance { get; } = new();

        public bool Equals((string InsertText, CommandCompletionKind Kind) x, (string InsertText, CommandCompletionKind Kind) y) =>
            x.Kind == y.Kind && StringComparer.OrdinalIgnoreCase.Equals(x.InsertText, y.InsertText);

        public int GetHashCode((string InsertText, CommandCompletionKind Kind) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.InsertText), obj.Kind);
    }

    private readonly record struct CompletionToken(
        string Value,
        int ReplacementStart,
        int ReplacementLength,
        bool IsQuoted,
        bool IsCommandPosition)
    {
        public static CompletionToken Parse(string text, int caretOffset)
        {
            var quote = '\0';
            var statementStart = 0;
            var rawTokenStart = 0;
            var escaped = false;

            for (var index = 0; index < caretOffset; index++)
            {
                var character = text[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '`')
                {
                    escaped = true;
                    continue;
                }

                if (quote != '\0')
                {
                    if (character == quote) quote = '\0';
                    continue;
                }

                if (character is '\'' or '"')
                {
                    quote = character;
                    continue;
                }

                if (StatementSeparators.Contains(character))
                {
                    statementStart = index + 1;
                    rawTokenStart = index + 1;
                    continue;
                }

                if (char.IsWhiteSpace(character))
                {
                    rawTokenStart = index + 1;
                }
            }

            while (rawTokenStart < caretOffset && char.IsWhiteSpace(text[rawTokenStart])) rawTokenStart++;
            var tokenStart = rawTokenStart;
            var isQuoted = tokenStart < caretOffset && text[tokenStart] is '\'' or '"';
            if (isQuoted) tokenStart++;
            var value = text[tokenStart..caretOffset];
            var commandPosition = string.IsNullOrWhiteSpace(text[statementStart..rawTokenStart]);
            return new CompletionToken(value, tokenStart, caretOffset - tokenStart, isQuoted, commandPosition);
        }
    }
}

internal static class StringExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) => value.IndexOfAny(characters) >= 0;
}
