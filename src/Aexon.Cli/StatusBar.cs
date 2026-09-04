using Aexon.Core.Messages;

namespace Aexon.Cli;

/// <summary>
/// Represents the formatted status bar payload.
/// </summary>
internal readonly record struct StatusBarSnapshot(
    string Model,
    TokenUsage Usage,
    TimeSpan SessionDuration);

/// <summary>
/// Renders a minimal status line for the interactive CLI.
/// </summary>
internal sealed class StatusBar
{
    public void Refresh(StatusBarSnapshot snapshot)
    {
        if (Console.IsOutputRedirected)
            return;

        var line = Format(snapshot);
        if (!TryWriteBottomLine(line))
            Console.WriteLine(line);
    }

    internal static string Format(StatusBarSnapshot snapshot)
    {
        return $"[status] model {snapshot.Model} | tokens {snapshot.Usage.TotalInputTokens:N0}/{snapshot.Usage.OutputTokens:N0} in/out | session {FormatDuration(snapshot.SessionDuration)}";
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static bool TryWriteBottomLine(string line)
    {
        try
        {
            var width = Math.Max(1, Console.WindowWidth);
            var bufferHeight = Math.Max(1, Console.BufferHeight);
            var cursorTop = Console.CursorTop;
            var cursorLeft = Console.CursorLeft;
            var row = Math.Min(bufferHeight - 1, Console.WindowTop + Console.WindowHeight - 1);
            var paddedLine = line.Length >= width
                ? line[..Math.Max(0, width - 1)]
                : line.PadRight(width - 1);

            Console.SetCursorPosition(0, row);
            Console.Write(paddedLine);
            Console.SetCursorPosition(cursorLeft, Math.Min(cursorTop, Console.BufferHeight - 1));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
