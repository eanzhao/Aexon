using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;

namespace Aexon.Cli;

/// <summary>
/// Renders lightweight tool execution progress in the CLI.
/// </summary>
internal sealed class ToolProgressRenderer
{
    private readonly Dictionary<string, ActiveToolExecution> _activeExecutions =
        new(StringComparer.Ordinal);

    public void Start(string toolUseId, string toolName, JsonElement input)
    {
        var summary = SummarizeParameters(input);
        _activeExecutions[toolUseId] = new ActiveToolExecution(toolName, summary, Stopwatch.StartNew());

        var plain = string.IsNullOrWhiteSpace(summary)
            ? $"[tool:start] {toolName}"
            : $"[tool:start] {toolName} {summary}";
        var markup = string.IsNullOrWhiteSpace(summary)
            ? $"[deepskyblue1]⠋[/] [bold]{Markup.Escape(toolName)}[/]"
            : $"[deepskyblue1]⠋[/] [bold]{Markup.Escape(toolName)}[/] [grey]{Markup.Escape(summary)}[/]";

        WriteLine(plain, markup);
    }

    public void ReportProgress(string toolUseId, string message)
    {
        if (!_activeExecutions.TryGetValue(toolUseId, out var execution) ||
            string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var plain = $"[tool:progress] {execution.ToolName} {message}";
        var markup =
            $"[grey]…[/] [bold]{Markup.Escape(execution.ToolName)}[/] [grey]{Markup.Escape(message)}[/]";
        WriteLine(plain, markup);
    }

    public void Complete(string toolUseId, string toolName, bool isError)
    {
        if (!_activeExecutions.Remove(toolUseId, out var execution))
            execution = new ActiveToolExecution(toolName, string.Empty, null);

        var elapsedText = FormatElapsed(execution.Stopwatch?.Elapsed ?? TimeSpan.Zero);
        var summarySuffix = string.IsNullOrWhiteSpace(execution.Summary)
            ? string.Empty
            : $" {execution.Summary}";
        var markupSummarySuffix = string.IsNullOrWhiteSpace(execution.Summary)
            ? string.Empty
            : $" [grey]{Markup.Escape(execution.Summary)}[/]";

        var plain = isError
            ? $"[tool:failed] {toolName} ({elapsedText}){summarySuffix}"
            : $"[tool:done] {toolName} ({elapsedText}){summarySuffix}";
        var markup = isError
            ? $"[red]✗[/] [bold]{Markup.Escape(toolName)}[/] [grey]({Markup.Escape(elapsedText)})[/]{markupSummarySuffix}"
            : $"[green]✓[/] [bold]{Markup.Escape(toolName)}[/] [grey]({Markup.Escape(elapsedText)})[/]{markupSummarySuffix}";

        WriteLine(plain, markup);
    }

    internal static string SummarizeParameters(JsonElement input, int maxLength = 120)
    {
        var summary = FormatValue(input, propertyName: null, depth: 0);
        if (summary.Length <= maxLength)
            return summary;

        return $"{summary[..Math.Max(0, maxLength - 3)]}...";
    }

    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
            return $"{Math.Max(1, elapsed.TotalMilliseconds):0}ms";

        if (elapsed.TotalMinutes < 1)
            return $"{elapsed.TotalSeconds:0.0}s";

        if (elapsed.TotalHours < 1)
            return $"{elapsed.Minutes}m {elapsed.Seconds}s";

        return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s";
    }

    private static string FormatValue(JsonElement value, string? propertyName, int depth)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => FormatObject(value, depth),
            JsonValueKind.Array => FormatArray(value, depth),
            JsonValueKind.String => FormatString(value.GetString(), propertyName),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "undefined",
            _ => value.GetRawText(),
        };
    }

    private static string FormatObject(JsonElement value, int depth)
    {
        if (depth > 0)
            return "{...}";

        var properties = value.EnumerateObject()
            .OrderBy(property => GetPriority(property.Name))
            .ThenBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (properties.Length == 0)
            return "{}";

        return string.Join(", ", properties.Select(property =>
            $"{property.Name}={FormatValue(property.Value, property.Name, depth + 1)}"));
    }

    private static string FormatArray(JsonElement value, int depth)
    {
        if (depth > 0)
            return $"[{value.GetArrayLength()} item(s)]";

        var items = value.EnumerateArray()
            .Take(3)
            .Select(item => FormatValue(item, propertyName: null, depth + 1))
            .ToArray();

        var suffix = value.GetArrayLength() > items.Length ? ", ..." : string.Empty;
        return $"[{string.Join(", ", items)}{suffix}]";
    }

    private static string FormatString(string? value, string? propertyName)
    {
        var normalized = NormalizeWhitespace(value);
        if (string.IsNullOrEmpty(normalized))
            return "\"\"";

        if (IsLargeContentProperty(propertyName))
            return $"<{normalized.Length} chars>";

        return normalized;
    }

    private static bool IsLargeContentProperty(string? propertyName) =>
        propertyName is "content" or "old_string" or "new_string" or "patch" or "body" or "prompt";

    private static int GetPriority(string propertyName) =>
        propertyName switch
        {
            "command" => 0,
            "file_path" => 1,
            "path" => 2,
            "description" => 3,
            _ => 10,
        };

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void WriteLine(string plain, string markup)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(plain);
            return;
        }

        AnsiConsole.MarkupLine(markup);
    }

    private sealed record ActiveToolExecution(
        string ToolName,
        string Summary,
        Stopwatch? Stopwatch);
}
