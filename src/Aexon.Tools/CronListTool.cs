using System.Text;
using System.Text.Json;
using Aexon.Core.Cron;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Lists all scheduled cron jobs and optionally their execution history.
/// </summary>
public sealed class CronListTool : ITool
{
    private readonly ICronRuntime _runtime;

    public CronListTool(ICronRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Name => "CronList";

    public string[] Aliases => ["CronListTool"];

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("List all scheduled cron jobs and optionally show execution history.");

    public JsonElement GetInputSchema()
    {
        var schema = """
        {
            "type": "object",
            "properties": {
                "job_id": {
                    "type": "string",
                    "description": "Optional: filter execution history by job id"
                },
                "include_history": {
                    "type": "boolean",
                    "description": "Whether to include recent execution history (default: false)"
                }
            },
            "additionalProperties": false
        }
        """;

        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        var jobs = _runtime.ListJobs();
        var rendered = RenderJobs(jobs);
        return Task.FromResult($"""
            List all scheduled cron jobs and their status.
            Set include_history to true to see recent execution records.
            Optionally filter history by job_id.

            Current cron jobs:
            {rendered}
            """);
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var jobs = _runtime.ListJobs();
        var sb = new StringBuilder();
        sb.AppendLine("Scheduled cron jobs");
        sb.AppendLine();
        sb.Append(RenderJobs(jobs));

        var includeHistory = input.TryGetProperty("include_history", out var historyProp) &&
                             historyProp.ValueKind == JsonValueKind.True;

        if (includeHistory)
        {
            string? jobId = null;
            if (input.TryGetProperty("job_id", out var jobIdProp) &&
                jobIdProp.ValueKind == JsonValueKind.String)
            {
                jobId = jobIdProp.GetString();
            }

            var history = _runtime.ListHistory(jobId);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Execution history");
            sb.AppendLine();
            sb.Append(RenderHistory(history));
        }

        return Task.FromResult(ToolResult.Success(sb.ToString().TrimEnd()));
    }

    public bool IsReadOnly(JsonElement input) => true;

    public bool IsConcurrencySafe(JsonElement input) => true;

    public string GetUserFacingName(JsonElement? input = null) => "Cron list";

    public string? GetActivityDescription(JsonElement? input) => "Listing cron jobs";

    private static string RenderJobs(IReadOnlyList<CronJob> jobs)
    {
        if (jobs.Count == 0)
            return "(no cron jobs)";

        var sb = new StringBuilder();
        foreach (var job in jobs)
        {
            sb.Append("- ")
                .Append(job.Id)
                .Append(" [")
                .Append(job.Enabled ? "enabled" : "disabled")
                .Append("] ")
                .AppendLine(job.Schedule);
            if (job.Kind == CronJobKind.Wakeup)
            {
                sb.Append("  prompt: ").AppendLine(job.Prompt ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(job.SessionId))
                    sb.Append("  session_id: ").AppendLine(job.SessionId);
            }
            else
            {
                sb.Append("  command: ").AppendLine(job.Command);
            }

            if (!string.IsNullOrWhiteSpace(job.Description))
                sb.Append("  description: ").AppendLine(job.Description);
            if (job.LastRunAt != null)
                sb.Append("  last_run: ").AppendLine(job.LastRunAt.Value.ToString("u"));
            if (job.NextRunAt != null)
                sb.Append("  next_run: ").AppendLine(job.NextRunAt.Value.ToString("u"));
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderHistory(IReadOnlyList<CronExecutionRecord> records)
    {
        if (records.Count == 0)
            return "(no execution history)";

        var sb = new StringBuilder();
        foreach (var record in records)
        {
            sb.Append("- ")
                .Append(record.Id)
                .Append(" [")
                .Append(record.Success ? "success" : "failed")
                .Append("] job=")
                .Append(record.JobId)
                .Append(" started=")
                .AppendLine(record.StartedAt.ToString("u"));
            if (record.CompletedAt != null)
                sb.Append("  completed: ").AppendLine(record.CompletedAt.Value.ToString("u"));
            if (!string.IsNullOrWhiteSpace(record.Output))
            {
                var output = record.Output.Length > 200
                    ? $"{record.Output[..197]}..."
                    : record.Output;
                sb.Append("  output: ").AppendLine(output);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
