using System.Text.Json;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Pauses tool execution for a bounded amount of time.
/// </summary>
public sealed class SleepTool : ITool
{
    public string Name => "Sleep";

    public string[] Aliases => ["SleepTool"];

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Pause execution for a bounded number of seconds.");

    public JsonElement GetInputSchema()
    {
        var schema = """
        {
            "type": "object",
            "properties": {
                "seconds": {
                    "type": "integer",
                    "minimum": 1,
                    "maximum": 3600,
                    "description": "How many seconds to sleep for"
                }
            },
            "required": ["seconds"],
            "additionalProperties": false
        }
        """;

        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context) =>
        Task.FromResult("""
            Pause execution for a short, bounded wait.

            Use this when you need to wait before checking on an external process again.
            """);

    public Task<ValidationResult> ValidateInputAsync(
        JsonElement input,
        ToolExecutionContext context)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return Task.FromResult(ValidationResult.Invalid("Input must be an object."));

        return Task.FromResult(
            TryGetSeconds(input, out _, out var error)
                ? ValidationResult.Valid()
                : ValidationResult.Invalid(error!));
    }

    public bool IsReadOnly(JsonElement input) => true;

    public string GetUserFacingName(JsonElement? input = null) => "Sleep";

    public string? GetActivityDescription(JsonElement? input) => "Sleeping";

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSeconds(input, out var seconds, out var error))
            return ToolResult.Error(error!);

        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        return ToolResult.Success($"Slept for {seconds} {FormatUnits(seconds)}.");
    }

    private static bool TryGetSeconds(
        JsonElement input,
        out int seconds,
        out string? error)
    {
        seconds = 0;
        error = null;

        if (!input.TryGetProperty("seconds", out var secondsProp))
        {
            error = "seconds is required.";
            return false;
        }

        if (secondsProp.ValueKind != JsonValueKind.Number || !secondsProp.TryGetInt32(out seconds))
        {
            error = "seconds must be an integer.";
            return false;
        }

        if (seconds < 1 || seconds > 3600)
        {
            error = "seconds must be between 1 and 3600.";
            return false;
        }

        return true;
    }

    private static string FormatUnits(int seconds) =>
        seconds == 1 ? "second" : "seconds";
}
