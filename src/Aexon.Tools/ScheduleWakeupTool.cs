using System.Text.Json;
using Aexon.Core.Cron;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Schedules a one-shot wakeup for the active loop session.
/// </summary>
public sealed class ScheduleWakeupTool : ITool
{
    private readonly ICronRuntime _runtime;

    public ScheduleWakeupTool(ICronRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Name => "ScheduleWakeup";

    public string[] Aliases => ["ScheduleWakeupTool"];

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Schedule a one-shot wakeup prompt for the current loop session.");

    public JsonElement GetInputSchema()
    {
        var schema = """
        {
            "type": "object",
            "properties": {
                "delaySeconds": {
                    "type": "integer",
                    "minimum": 60,
                    "maximum": 3600,
                    "description": "How many seconds to wait before firing the wakeup"
                },
                "prompt": {
                    "type": "string",
                    "description": "The prompt payload to fire when the wakeup triggers"
                },
                "reason": {
                    "type": "string",
                    "description": "Why this wakeup is being scheduled"
                }
            },
            "required": ["delaySeconds", "prompt", "reason"],
            "additionalProperties": false
        }
        """;

        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context) =>
        Task.FromResult("""
            Schedule a one-shot wakeup for the active loop session.

            Use this when you want the scheduler to deliver a follow-up prompt later without blocking on Sleep.
            """);

    public Task<ValidationResult> ValidateInputAsync(
        JsonElement input,
        ToolExecutionContext context)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return Task.FromResult(ValidationResult.Invalid("Input must be an object."));

        if (!HasActiveLoopSession(context))
            return Task.FromResult(ValidationResult.Invalid("ScheduleWakeup requires an active loop session."));

        return Task.FromResult(
            TryParse(input, out _, out _, out _, out var error)
                ? ValidationResult.Valid()
                : ValidationResult.Invalid(error!));
    }

    public string GetUserFacingName(JsonElement? input = null) => "Schedule wakeup";

    public string? GetActivityDescription(JsonElement? input) => "Scheduling wakeup";

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!HasActiveLoopSession(context))
            return Task.FromResult(ToolResult.Error("ScheduleWakeup requires an active loop session."));

        if (!TryParse(input, out var delaySeconds, out var prompt, out var reason, out var error))
            return Task.FromResult(ToolResult.Error(error!));

        try
        {
            var job = _runtime.CreateWakeupJob(
                context.LoopSessionId!,
                TimeSpan.FromSeconds(delaySeconds),
                prompt!,
                reason!);

            return Task.FromResult(ToolResult.Success(
                $"Scheduled wakeup '{job.Id}' in {delaySeconds} seconds. Reason: {reason}."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
    }

    private static bool HasActiveLoopSession(ToolExecutionContext context) =>
        !string.IsNullOrWhiteSpace(context.LoopSessionId);

    private static bool TryParse(
        JsonElement input,
        out int delaySeconds,
        out string? prompt,
        out string? reason,
        out string? error)
    {
        delaySeconds = 0;
        prompt = null;
        reason = null;
        error = null;

        if (!TryGetDelaySeconds(input, out delaySeconds, out error))
            return false;

        if (!TryGetString(input, "prompt", out prompt))
        {
            error = "prompt is required.";
            return false;
        }

        if (!TryGetString(input, "reason", out reason))
        {
            error = "reason is required.";
            return false;
        }

        return true;
    }

    private static bool TryGetDelaySeconds(
        JsonElement input,
        out int delaySeconds,
        out string? error)
    {
        delaySeconds = 0;
        error = null;

        if (!input.TryGetProperty("delaySeconds", out var delayProp))
        {
            error = "delaySeconds is required.";
            return false;
        }

        if (delayProp.ValueKind != JsonValueKind.Number || !delayProp.TryGetInt32(out delaySeconds))
        {
            error = "delaySeconds must be an integer.";
            return false;
        }

        if (delaySeconds < 60 || delaySeconds > 3600)
        {
            error = "delaySeconds must be between 60 and 3600.";
            return false;
        }

        return true;
    }

    private static bool TryGetString(
        JsonElement input,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!input.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(prop.GetString()))
        {
            return false;
        }

        value = prop.GetString()!.Trim();
        return true;
    }
}
