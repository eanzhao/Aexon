using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aexon.Core.Agents;
using Aexon.Core.Tools;

namespace Aexon.Tools;

public sealed class MonitorToolInput
{
    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }

    [JsonPropertyName("process_id")]
    public string? ProcessId { get; set; }

    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("follow")]
    public bool Follow { get; set; }

    [JsonPropertyName("poll_ms")]
    public int? PollMs { get; set; }

    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; set; }
}

public sealed class MonitorTool : ITool
{
    private readonly IAgentTaskRuntime _runtime;

    public MonitorTool(IAgentTaskRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Name => "Monitor";

    public string[] Aliases => ["MonitorTool"];

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Monitor a task or background process output and optionally stream new lines until it finishes.");

    public JsonElement GetInputSchema() => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "task_id": { "type": "string" },
            "process_id": { "type": "string" },
            "offset": { "type": "integer" },
            "limit": { "type": "integer" },
            "follow": { "type": "boolean" },
            "poll_ms": { "type": "integer" },
            "timeout_ms": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public Task<string> GetPromptAsync(ToolPromptContext context) =>
        Task.FromResult("""
            Use Monitor for real-time task or background-process output.

            Pass process_id to monitor a specific background run, or task_id to automatically select the latest active run for that task.
            Set follow=true to stream each new output line as a progress event until the run finishes or the timeout expires.
            """);

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MonitorToolInput>(input) ?? new MonitorToolInput();
            if (string.IsNullOrWhiteSpace(parsed.TaskId) && string.IsNullOrWhiteSpace(parsed.ProcessId))
                return Task.FromResult(ValidationResult.Invalid("task_id or process_id is required."));
            if (parsed.Offset is < 0)
                return Task.FromResult(ValidationResult.Invalid("offset must be 0 or greater."));
            if (parsed.Limit is <= 0)
                return Task.FromResult(ValidationResult.Invalid("limit must be greater than 0."));
            if (parsed.PollMs is <= 0)
                return Task.FromResult(ValidationResult.Invalid("poll_ms must be greater than 0."));
            if (parsed.TimeoutMs is <= 0)
                return Task.FromResult(ValidationResult.Invalid("timeout_ms must be greater than 0."));
            return Task.FromResult(ValidationResult.Valid());
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ValidationResult.Invalid(ex.Message));
        }
    }

    public bool IsReadOnly(JsonElement input) => true;

    public bool IsConcurrencySafe(JsonElement input) => true;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<MonitorToolInput>(input) ?? new MonitorToolInput();
        if (string.IsNullOrWhiteSpace(parsed.TaskId) && string.IsNullOrWhiteSpace(parsed.ProcessId))
            return ToolResult.Error("task_id or process_id is required.");

        if (!AgentBackgroundRunSelector.TrySelect(
                _runtime,
                parsed.TaskId,
                parsed.ProcessId,
                out var selection,
                out var error))
        {
            return ToolResult.Error(error!);
        }

        var offset = parsed.Offset ?? 0;
        var timeout = parsed.TimeoutMs.HasValue
            ? TimeSpan.FromMilliseconds(parsed.TimeoutMs.Value)
            : TimeSpan.FromSeconds(30);
        var poll = TimeSpan.FromMilliseconds(parsed.PollMs ?? 500);
        var startedAt = DateTimeOffset.UtcNow;
        var emittedLineCount = 0;

        int Emit(AgentBackgroundRunOutputPage page)
        {
            foreach (var entry in page.Entries)
            {
                foreach (var line in SplitLines(entry))
                {
                    progress?.Report(new ToolProgress("", "monitor_line", line));
                    emittedLineCount++;
                }
            }

            return page.NextOffset;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!AgentStatusFormatter.TryGetOutputPage(
                    _runtime,
                    selection!.Run.Id,
                    offset,
                    parsed.Limit,
                    out var currentRun,
                    out var page,
                    out error))
            {
                return ToolResult.Error(error!);
            }

            offset = Emit(page);
            if (!parsed.Follow)
                return ToolResult.Success(BuildSnapshot(selection, currentRun!, page, emittedLineCount));

            if (AgentBackgroundRunWaiter.IsTerminal(currentRun!.Status))
            {
                // The run reached a terminal state, so no further output can be
                // appended. Drain anything written between the page read above and
                // the stop transition before reporting completion — the snapshot
                // count is otherwise read before the terminal status, so a final
                // line could be dropped under a concurrent producer.
                if (!AgentStatusFormatter.TryGetOutputPage(
                        _runtime,
                        selection.Run.Id,
                        offset,
                        limit: null,
                        out currentRun,
                        out var finalPage,
                        out error))
                {
                    return ToolResult.Error(error!);
                }

                Emit(finalPage);
                return ToolResult.Success(BuildCompletion(selection, currentRun!, emittedLineCount, startedAt));
            }

            if (DateTimeOffset.UtcNow - startedAt >= timeout)
            {
                return ToolResult.Error(
                    $"Monitor timed out after {Math.Round(timeout.TotalMilliseconds)}ms for {currentRun.Id} [{currentRun.Status}].");
            }

            await Task.Delay(poll, cancellationToken);
            parsed.Limit = null;
        }
    }

    public string GetUserFacingName(JsonElement? input = null) => "Monitor";

    public string? GetActivityDescription(JsonElement? input) => "Monitoring task";

    private static string BuildSnapshot(
        AgentBackgroundRunSelection selection,
        AgentBackgroundRun run,
        AgentBackgroundRunOutputPage page,
        int emittedLineCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Monitor snapshot for {run.Id} ({selection.SelectionReason})");
        builder.AppendLine($"Status: {run.Status}");
        builder.AppendLine($"Streamed lines: {emittedLineCount}");
        builder.AppendLine(AgentStatusFormatter.FormatOutputPage(run, page, includeRunHeader: false));
        return builder.ToString().TrimEnd();
    }

    private static string BuildCompletion(
        AgentBackgroundRunSelection selection,
        AgentBackgroundRun run,
        int emittedLineCount,
        DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return
            $"Monitor finished for {run.Id} ({selection.SelectionReason}) after {Math.Round(elapsed.TotalMilliseconds)}ms. " +
            $"Status: {run.Status}. Streamed lines: {emittedLineCount}.";
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        using var reader = new StringReader(value);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }
}
