using System.Text;
using Aexon.Core.Commands;

namespace Aexon.Cli;

internal sealed class LineEditor
{
    internal const int MaxHistoryEntries = 500;

    private readonly IReadOnlyList<string> _commandNames;
    private readonly LineEditorHistory _history;
    private readonly string _placeholder;
    private readonly string _prompt;
    private readonly Func<ConsoleKeyInfo> _readKey;
    private readonly string _workingDirectory;

    public LineEditor(
        CommandRegistry commandRegistry,
        List<string> history,
        string workingDirectory,
        string prompt = "claudesharp> ",
        string placeholder = "Type a message or / for commands",
        Func<ConsoleKeyInfo>? readKey = null)
    {
        _commandNames = commandRegistry.GetAll()
            .Select(command => $"/{command.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _history = new LineEditorHistory(history);
        _workingDirectory = workingDirectory;
        _prompt = prompt;
        _placeholder = placeholder;
        _readKey = readKey ?? (() => Console.ReadKey(intercept: true));
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ReadLine(cancellationToken));

    private string? ReadLine(CancellationToken cancellationToken)
    {
        if (!SupportsInteractiveConsole())
            return ReadFallbackLine();

        try
        {
            return ReadInteractiveLine(cancellationToken);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or NotSupportedException)
        {
            return ReadFallbackLine();
        }
    }

    private string? ReadFallbackLine()
    {
        Console.Write(_prompt);
        return Console.ReadLine();
    }

    private string? ReadInteractiveLine(CancellationToken cancellationToken)
    {
        var buffer = new LineEditorBuffer();
        var renderer = new LineEditorRenderer(_prompt, _placeholder);
        LineEditorCompletionSession? completionSession = null;
        _history.ResetNavigation();

        var originalTreatControlCAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        try
        {
            renderer.Render(buffer.Text, buffer.Cursor);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = _readKey();
                if (key.Key == ConsoleKey.Tab)
                {
                    if (TryApplyCompletion(buffer, ref completionSession))
                        renderer.Render(buffer.Text, buffer.Cursor);

                    continue;
                }

                completionSession = null;

                var outcome = HandleKey(key, buffer);
                if (outcome == LineEditorOutcome.None)
                    continue;

                if (outcome == LineEditorOutcome.Submit)
                {
                    var submitted = buffer.Text;
                    if (!string.IsNullOrWhiteSpace(submitted))
                        _history.AddEntry(submitted);
                    else
                        _history.ResetNavigation();

                    renderer.Render(buffer.Text, buffer.Cursor);
                    renderer.Finish();
                    return submitted;
                }

                if (outcome == LineEditorOutcome.Exit)
                {
                    renderer.Finish();
                    return null;
                }

                renderer.Render(buffer.Text, buffer.Cursor);
            }
        }
        finally
        {
            Console.TreatControlCAsInput = originalTreatControlCAsInput;
        }
    }

    private LineEditorOutcome HandleKey(ConsoleKeyInfo key, LineEditorBuffer buffer)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            switch (key.Key)
            {
                case ConsoleKey.A:
                    return buffer.MoveHome() ? LineEditorOutcome.Render : LineEditorOutcome.None;
                case ConsoleKey.C:
                    buffer.Clear();
                    _history.ResetNavigation();
                    return LineEditorOutcome.Render;
                case ConsoleKey.D:
                    return buffer.Length == 0 ? LineEditorOutcome.Exit : LineEditorOutcome.None;
                case ConsoleKey.E:
                    return buffer.MoveEnd() ? LineEditorOutcome.Render : LineEditorOutcome.None;
                case ConsoleKey.U:
                    return buffer.DeleteToStart() ? LineEditorOutcome.Render : LineEditorOutcome.None;
                case ConsoleKey.W:
                    return buffer.DeleteWordBack() ? LineEditorOutcome.Render : LineEditorOutcome.None;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Backspace:
                return buffer.Backspace() ? LineEditorOutcome.Render : LineEditorOutcome.None;
            case ConsoleKey.Delete:
                return buffer.DeleteForward() ? LineEditorOutcome.Render : LineEditorOutcome.None;
            case ConsoleKey.LeftArrow:
                return buffer.MoveLeft() ? LineEditorOutcome.Render : LineEditorOutcome.None;
            case ConsoleKey.RightArrow:
                return buffer.MoveRight() ? LineEditorOutcome.Render : LineEditorOutcome.None;
            case ConsoleKey.Home:
                return buffer.MoveHome() ? LineEditorOutcome.Render : LineEditorOutcome.None;
            case ConsoleKey.End:
                return buffer.MoveEnd() ? LineEditorOutcome.Render : LineEditorOutcome.None;
            case ConsoleKey.UpArrow:
            {
                var previous = _history.MovePrevious(buffer.Text);
                if (previous == null)
                    return LineEditorOutcome.None;

                buffer.SetText(previous);
                return LineEditorOutcome.Render;
            }

            case ConsoleKey.DownArrow:
            {
                var next = _history.MoveNext(buffer.Text);
                if (next == null)
                    return LineEditorOutcome.None;

                buffer.SetText(next);
                return LineEditorOutcome.Render;
            }

            case ConsoleKey.Enter:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    buffer.Insert(Environment.NewLine);
                    return LineEditorOutcome.Render;
                }

                return LineEditorOutcome.Submit;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Insert(key.KeyChar.ToString());
                    return LineEditorOutcome.Render;
                }

                return LineEditorOutcome.None;
        }
    }

    private bool TryApplyCompletion(
        LineEditorBuffer buffer,
        ref LineEditorCompletionSession? completionSession)
    {
        if (completionSession != null && completionSession.CanContinue(buffer.Text, buffer.Cursor))
            return completionSession.ApplyNext(buffer);

        completionSession = LineEditorCompletion.CreateSession(
            buffer.Text,
            buffer.Cursor,
            _commandNames,
            _workingDirectory);
        return completionSession != null && completionSession.ApplyNext(buffer);
    }

    private static bool SupportsInteractiveConsole()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        try
        {
            _ = Console.CursorTop;
            _ = Console.BufferWidth;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}

internal enum LineEditorOutcome
{
    None,
    Render,
    Submit,
    Exit,
}

internal sealed class LineEditorBuffer
{
    private readonly StringBuilder _text = new();

    public LineEditorBuffer(string text = "", int? cursor = null)
    {
        SetText(text, cursor);
    }

    public int Cursor { get; private set; }

    public int Length => _text.Length;

    public string Text => _text.ToString();

    public bool Backspace()
    {
        if (Cursor == 0)
            return false;

        _text.Remove(Cursor - 1, 1);
        Cursor--;
        return true;
    }

    public void Clear()
    {
        _text.Clear();
        Cursor = 0;
    }

    public bool DeleteForward()
    {
        if (Cursor >= _text.Length)
            return false;

        _text.Remove(Cursor, 1);
        return true;
    }

    public bool DeleteToStart()
    {
        if (Cursor == 0)
            return false;

        _text.Remove(0, Cursor);
        Cursor = 0;
        return true;
    }

    public bool DeleteWordBack()
    {
        if (Cursor == 0)
            return false;

        var start = Cursor;
        while (start > 0 && char.IsWhiteSpace(_text[start - 1]))
            start--;

        while (start > 0 && !char.IsWhiteSpace(_text[start - 1]))
            start--;

        _text.Remove(start, Cursor - start);
        Cursor = start;
        return true;
    }

    public void Insert(string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        _text.Insert(Cursor, value);
        Cursor += value.Length;
    }

    public bool MoveEnd()
    {
        if (Cursor == _text.Length)
            return false;

        Cursor = _text.Length;
        return true;
    }

    public bool MoveHome()
    {
        if (Cursor == 0)
            return false;

        Cursor = 0;
        return true;
    }

    public bool MoveLeft()
    {
        if (Cursor == 0)
            return false;

        Cursor--;
        return true;
    }

    public bool MoveRight()
    {
        if (Cursor >= _text.Length)
            return false;

        Cursor++;
        return true;
    }

    public void ReplaceRange(int start, int length, string replacement)
    {
        _text.Remove(start, length);
        _text.Insert(start, replacement);
        Cursor = start + replacement.Length;
    }

    public void SetText(string text, int? cursor = null)
    {
        _text.Clear();
        _text.Append(text);
        Cursor = cursor ?? _text.Length;
    }
}

internal sealed class LineEditorHistory
{
    private readonly List<string> _entries;
    private string _draft = string.Empty;
    private int _navigationIndex;

    public LineEditorHistory(List<string> entries)
    {
        _entries = entries;

        if (_entries.Count > LineEditor.MaxHistoryEntries)
            _entries.RemoveRange(0, _entries.Count - LineEditor.MaxHistoryEntries);

        _navigationIndex = _entries.Count;
    }

    public IReadOnlyList<string> Entries => _entries;

    public void AddEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            ResetNavigation();
            return;
        }

        _entries.Add(entry);
        if (_entries.Count > LineEditor.MaxHistoryEntries)
            _entries.RemoveRange(0, _entries.Count - LineEditor.MaxHistoryEntries);

        ResetNavigation();
    }

    public string? MoveNext(string currentText)
    {
        if (_entries.Count == 0)
            return null;

        if (_navigationIndex >= _entries.Count)
            return _draft;

        _navigationIndex++;
        if (_navigationIndex == _entries.Count)
            return _draft;

        return _entries[_navigationIndex];
    }

    public string? MovePrevious(string currentText)
    {
        if (_entries.Count == 0)
            return null;

        if (_navigationIndex == _entries.Count)
            _draft = currentText;

        if (_navigationIndex == 0)
            return _entries[0];

        _navigationIndex--;
        return _entries[_navigationIndex];
    }

    public void ResetNavigation()
    {
        _navigationIndex = _entries.Count;
        _draft = string.Empty;
    }
}

internal static class LineEditorCompletion
{
    public static LineEditorCompletionSession? CreateSession(
        string text,
        int cursor,
        IReadOnlyList<string> commandNames,
        string workingDirectory)
    {
        var commandCompletion = TryCreateCommandCompletion(text, cursor, commandNames);
        if (commandCompletion != null)
            return new LineEditorCompletionSession(text, cursor, commandCompletion.Value);

        var pathCompletion = TryCreatePathCompletion(text, cursor, workingDirectory);
        return pathCompletion != null
            ? new LineEditorCompletionSession(text, cursor, pathCompletion.Value)
            : null;
    }

    public static IReadOnlyList<string> MatchCommands(
        string prefix,
        IEnumerable<string> commandNames) =>
        commandNames
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<string> MatchPaths(string token, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(token))
            return [];

        var quotePrefix = token[0] is '\'' or '"' ? token[0].ToString() : string.Empty;
        var pathToken = quotePrefix.Length == 0 ? token : token[1..];
        if (pathToken.Length == 0)
            return [];

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directoryPrefix = GetDirectoryPrefix(pathToken);
        var filePrefix = GetFilePrefix(pathToken);
        var searchDirectory = ResolveSearchDirectory(directoryPrefix, workingDirectory, home);
        if (searchDirectory == null)
            return [];

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(searchDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return entries
            .Where(entry => Path.GetFileName(entry).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)
            .Select(entry => quotePrefix + BuildDisplayPath(directoryPrefix, entry))
            .ToList();
    }

    public static LineEditorCompletionSet? TryCreateCommandCompletion(
        string text,
        int cursor,
        IReadOnlyList<string> commandNames)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return null;

        var commandEnd = FindTokenEnd(text, 0);
        if (cursor > commandEnd)
            return null;

        var prefix = text[..cursor];
        var matches = MatchCommands(prefix, commandNames);
        return matches.Count == 0
            ? null
            : new LineEditorCompletionSet(0, commandEnd, matches);
    }

    public static LineEditorCompletionSet? TryCreatePathCompletion(
        string text,
        int cursor,
        string workingDirectory)
    {
        var tokenStart = FindTokenStart(text, cursor);
        var tokenEnd = FindTokenEnd(text, cursor);
        if (tokenStart == tokenEnd)
            return null;

        if (tokenStart == 0 && text.StartsWith('/'))
            return null;

        var token = text[tokenStart..tokenEnd];
        var matches = MatchPaths(token, workingDirectory);
        return matches.Count == 0
            ? null
            : new LineEditorCompletionSet(tokenStart, token.Length, matches);
    }

    private static string BuildDisplayPath(string directoryPrefix, string entryPath)
    {
        var separator = directoryPrefix.LastOrDefault(ch => ch is '/' or '\\');
        if (separator == default)
            separator = Path.DirectorySeparatorChar;

        var suffix = Directory.Exists(entryPath) ? separator.ToString() : string.Empty;
        return directoryPrefix + Path.GetFileName(entryPath) + suffix;
    }

    private static int FindTokenEnd(string text, int cursor)
    {
        var index = cursor;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static int FindTokenStart(string text, int cursor)
    {
        var index = Math.Clamp(cursor, 0, text.Length);
        while (index > 0 && !char.IsWhiteSpace(text[index - 1]))
            index--;

        return index;
    }

    private static string GetDirectoryPrefix(string token)
    {
        var index = token.LastIndexOfAny(['/', '\\']);
        return index < 0
            ? string.Empty
            : token[..(index + 1)];
    }

    private static string GetFilePrefix(string token)
    {
        var index = token.LastIndexOfAny(['/', '\\']);
        return index < 0
            ? token
            : token[(index + 1)..];
    }

    private static string? ResolveSearchDirectory(string directoryPrefix, string workingDirectory, string homeDirectory)
    {
        if (string.IsNullOrEmpty(directoryPrefix))
            return workingDirectory;

        if (directoryPrefix == "~" || directoryPrefix.StartsWith("~/", StringComparison.Ordinal))
        {
            var relative = directoryPrefix.Length == 1
                ? string.Empty
                : directoryPrefix[2..];
            var combined = Path.Combine(homeDirectory, relative);
            return Directory.Exists(combined) ? combined : null;
        }

        if (Path.IsPathRooted(directoryPrefix))
            return Directory.Exists(directoryPrefix) ? directoryPrefix : null;

        var fullPath = Path.GetFullPath(Path.Combine(workingDirectory, directoryPrefix));
        return Directory.Exists(fullPath) ? fullPath : null;
    }
}

internal sealed class LineEditorCompletionSession
{
    private readonly int _cursor;
    private int _index = -1;
    private readonly string _originalText;
    private readonly LineEditorCompletionSet _set;

    public LineEditorCompletionSession(string originalText, int cursor, LineEditorCompletionSet set)
    {
        _originalText = originalText;
        _cursor = cursor;
        _set = set;
    }

    public bool ApplyNext(LineEditorBuffer buffer)
    {
        if (_set.Matches.Count == 0)
            return false;

        _index = (_index + 1) % _set.Matches.Count;
        var replacement = _set.Matches[_index];
        buffer.SetText(ApplyReplacement(replacement), _set.Start + replacement.Length);
        return true;
    }

    public bool CanContinue(string text, int cursor)
    {
        if (text == _originalText && cursor == _cursor)
            return true;

        return _set.Matches.Any(match =>
            text == ApplyReplacement(match) &&
            cursor == _set.Start + match.Length);
    }

    private string ApplyReplacement(string replacement) =>
        string.Concat(
            _originalText.AsSpan(0, _set.Start),
            replacement,
            _originalText.AsSpan(_set.Start + _set.Length));
}

internal readonly record struct LineEditorCompletionSet(
    int Start,
    int Length,
    IReadOnlyList<string> Matches);

internal static class LineEditorHistoryStore
{
    public static List<string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(Decode)
                .TakeLast(LineEditor.MaxHistoryEntries)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static Task SaveAsync(
        string path,
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var lines = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .TakeLast(LineEditor.MaxHistoryEntries)
            .Select(Encode)
            .ToArray();
        return File.WriteAllLinesAsync(path, lines, cancellationToken);
    }

    private static string Decode(string value)
    {
        var builder = new StringBuilder(value.Length);
        var escaped = false;

        foreach (var ch in value)
        {
            if (!escaped)
            {
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                builder.Append(ch);
                continue;
            }

            builder.Append(ch switch
            {
                'n' => '\n',
                '\\' => '\\',
                _ => ch,
            });
            escaped = false;
        }

        if (escaped)
            builder.Append('\\');

        return builder.ToString();
    }

    private static string Encode(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}

internal sealed class LineEditorRenderer
{
    private readonly int _anchorTop;
    private readonly string _placeholder;
    private readonly string _prompt;
    private LineEditorLayout _lastLayout;
    private int _renderedLineCount;

    public LineEditorRenderer(string prompt, string placeholder)
    {
        _prompt = prompt;
        _placeholder = placeholder;
        _anchorTop = Console.CursorTop;
        _lastLayout = BuildLayout(string.Empty, 0, prompt, placeholder, GetConsoleWidth());
    }

    public void Finish()
    {
        var targetTop = _anchorTop + _lastLayout.EndLine;
        try
        {
            Console.SetCursorPosition(_lastLayout.EndColumn, targetTop);
        }
        catch (Exception ex) when (
            ex is IOException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            Console.SetCursorPosition(0, Console.CursorTop);
        }

        Console.WriteLine();
    }

    public void Render(string text, int cursor)
    {
        var width = GetConsoleWidth();
        var layout = BuildLayout(text, cursor, _prompt, _placeholder, width);
        var originalColor = Console.ForegroundColor;
        var canToggleCursorVisibility = OperatingSystem.IsWindows();
        var originalCursorVisible = true;

        try
        {
            if (canToggleCursorVisibility)
            {
                originalCursorVisible = Console.CursorVisible;
                Console.CursorVisible = false;
            }

            var lineCount = Math.Max(_renderedLineCount, layout.Lines.Count);
            var clearWidth = Math.Max(1, width - 1);
            var clearLine = new string(' ', clearWidth);

            for (var index = 0; index < lineCount; index++)
            {
                Console.SetCursorPosition(0, _anchorTop + index);
                Console.Write(clearLine);
                Console.SetCursorPosition(0, _anchorTop + index);

                if (index >= layout.Lines.Count)
                    continue;

                var line = layout.Lines[index];
                Console.Write(line.Prefix);
                if (line.IsDim && line.Content.Length > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(line.Content);
                    Console.ForegroundColor = originalColor;
                    continue;
                }

                Console.Write(line.Content);
            }

            Console.SetCursorPosition(layout.CursorColumn, _anchorTop + layout.CursorLine);
        }
        finally
        {
            Console.ForegroundColor = originalColor;
            if (canToggleCursorVisibility)
                Console.CursorVisible = originalCursorVisible;
        }

        _lastLayout = layout;
        _renderedLineCount = layout.Lines.Count;
    }

    private static LineEditorLayout BuildLayout(
        string text,
        int cursor,
        string prompt,
        string placeholder,
        int width)
    {
        var visibleText = text.Length == 0 ? placeholder : text;
        var isPlaceholder = text.Length == 0;
        var prefixLength = prompt.Length;
        var continuationPrefix = new string(' ', prefixLength);
        var contentWidth = Math.Max(1, width - prefixLength - 1);
        var lines = new List<LineEditorDisplayLine>();
        var builder = new StringBuilder();
        var currentPrefix = prompt;
        var currentColumn = 0;
        var currentLine = 0;
        var consumed = 0;
        var cursorLine = 0;
        var cursorColumn = prefixLength;

        void StartNextLine()
        {
            lines.Add(new LineEditorDisplayLine(currentPrefix, builder.ToString(), isPlaceholder));
            builder.Clear();
            currentPrefix = continuationPrefix;
            currentColumn = 0;
            currentLine++;
        }

        if (!isPlaceholder && cursor == 0)
        {
            cursorLine = 0;
            cursorColumn = prefixLength;
        }

        foreach (var ch in visibleText)
        {
            if (!isPlaceholder && consumed == cursor)
            {
                cursorLine = currentLine;
                cursorColumn = prefixLength + currentColumn;
            }

            if (ch == '\r')
            {
                if (!isPlaceholder)
                    consumed++;

                continue;
            }

            if (ch == '\n')
            {
                StartNextLine();
                if (!isPlaceholder)
                    consumed++;

                continue;
            }

            if (currentColumn == contentWidth)
                StartNextLine();

            builder.Append(ch);
            currentColumn++;

            if (!isPlaceholder)
                consumed++;
        }

        if (!isPlaceholder && consumed == cursor)
        {
            cursorLine = currentLine;
            cursorColumn = prefixLength + currentColumn;
        }

        if (builder.Length == 0 && lines.Count == 0)
            lines.Add(new LineEditorDisplayLine(prompt, string.Empty, isPlaceholder));
        else
            lines.Add(new LineEditorDisplayLine(currentPrefix, builder.ToString(), isPlaceholder));

        var lastLine = lines[^1];
        return new LineEditorLayout(
            lines,
            cursorLine,
            cursorColumn,
            lines.Count - 1,
            lastLine.Prefix.Length + lastLine.Content.Length);
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Math.Max(20, Console.BufferWidth);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return 80;
        }
    }
}

internal readonly record struct LineEditorDisplayLine(
    string Prefix,
    string Content,
    bool IsDim);

internal readonly record struct LineEditorLayout(
    IReadOnlyList<LineEditorDisplayLine> Lines,
    int CursorLine,
    int CursorColumn,
    int EndLine,
    int EndColumn);
