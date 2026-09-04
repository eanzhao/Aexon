using System.Text.Json;
using Aexon.Core.Storage;

namespace Aexon.Core.Cron;

/// <summary>
/// Represents a restored snapshot of cron state.
/// </summary>
public sealed class CronStateSnapshot
{
    public IReadOnlyList<CronJob> Jobs { get; init; } = [];
    public IReadOnlyList<CronExecutionRecord> History { get; init; } = [];
}

/// <summary>
/// Serializes cron runtime state into transcript metadata and restores it on resume.
/// </summary>
public static class CronPersistence
{
    public const string JobEventType = "cron-job";
    public const string JobDeletedEventType = "cron-job-deleted";
    public const string ExecutionEventType = "cron-execution";

    public static TranscriptMetadataEntry CreateJobEntry(
        CronJob job,
        DateTimeOffset? recordedAt = null) =>
        new(
            JobEventType,
            JsonSerializer.SerializeToElement(new CronJobPayload
            {
                Kind = job.Kind,
                Id = job.Id,
                Schedule = job.Schedule,
                Command = job.Command,
                Description = job.Description,
                Enabled = job.Enabled,
                RunOnce = job.RunOnce,
                SessionId = job.SessionId,
                Prompt = job.Prompt,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt,
                LastRunAt = job.LastRunAt,
                NextRunAt = job.NextRunAt,
            }),
            recordedAt ?? job.UpdatedAt);

    public static TranscriptMetadataEntry CreateJobDeletedEntry(
        string jobId,
        DateTimeOffset? recordedAt = null) =>
        new(
            JobDeletedEventType,
            JsonSerializer.SerializeToElement(new DeletedJobPayload
            {
                Id = jobId,
            }),
            recordedAt ?? DateTimeOffset.UtcNow);

    public static TranscriptMetadataEntry CreateExecutionEntry(
        CronExecutionRecord record,
        DateTimeOffset? recordedAt = null) =>
        new(
            ExecutionEventType,
            JsonSerializer.SerializeToElement(new ExecutionPayload
            {
                Id = record.Id,
                JobId = record.JobId,
                StartedAt = record.StartedAt,
                CompletedAt = record.CompletedAt,
                Success = record.Success,
                Output = record.Output,
            }),
            recordedAt ?? record.StartedAt);

    public static CronStateSnapshot Restore(
        IReadOnlyList<TranscriptMetadataEntry> metadataEntries)
    {
        var jobs = new Dictionary<string, CronJob>(StringComparer.OrdinalIgnoreCase);
        var history = new List<CronExecutionRecord>();

        foreach (var entry in metadataEntries)
        {
            switch (entry.EventType)
            {
                case JobEventType:
                    if (TryReadPayload(entry.Payload, out CronJobPayload? jobPayload) &&
                        jobPayload != null &&
                        !string.IsNullOrWhiteSpace(jobPayload.Id))
                    {
                        jobs[jobPayload.Id] = new CronJob
                        {
                            Kind = jobPayload.Kind,
                            Id = jobPayload.Id,
                            Schedule = jobPayload.Schedule,
                            Command = jobPayload.Command,
                            Description = jobPayload.Description,
                            Enabled = jobPayload.Enabled,
                            RunOnce = jobPayload.RunOnce,
                            SessionId = jobPayload.SessionId,
                            Prompt = jobPayload.Prompt,
                            CreatedAt = jobPayload.CreatedAt,
                            UpdatedAt = jobPayload.UpdatedAt,
                            LastRunAt = jobPayload.LastRunAt,
                            NextRunAt = jobPayload.NextRunAt,
                        };
                    }

                    break;

                case JobDeletedEventType:
                    if (TryReadPayload(entry.Payload, out DeletedJobPayload? deletedJob) &&
                        deletedJob != null &&
                        !string.IsNullOrWhiteSpace(deletedJob.Id))
                    {
                        jobs.Remove(deletedJob.Id);
                    }

                    break;

                case ExecutionEventType:
                    if (TryReadPayload(entry.Payload, out ExecutionPayload? execPayload) &&
                        execPayload != null &&
                        !string.IsNullOrWhiteSpace(execPayload.Id))
                    {
                        history.Add(new CronExecutionRecord
                        {
                            Id = execPayload.Id,
                            JobId = execPayload.JobId,
                            StartedAt = execPayload.StartedAt,
                            CompletedAt = execPayload.CompletedAt,
                            Success = execPayload.Success,
                            Output = execPayload.Output,
                        });
                    }

                    break;
            }
        }

        return new CronStateSnapshot
        {
            Jobs = jobs.Values
                .OrderBy(j => j.CreatedAt)
                .ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            History = history
                .OrderByDescending(r => r.StartedAt)
                .ToArray(),
        };
    }

    private static bool TryReadPayload<T>(
        JsonElement? payload,
        out T? value)
    {
        value = default;
        if (payload is not JsonElement element ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        try
        {
            value = element.Deserialize<T>();
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class CronJobPayload
    {
        public CronJobKind Kind { get; init; } = CronJobKind.Command;
        public required string Id { get; init; }
        public required string Schedule { get; init; }
        public required string Command { get; init; }
        public string? Description { get; init; }
        public bool Enabled { get; init; } = true;
        public bool RunOnce { get; init; }
        public string? SessionId { get; init; }
        public string? Prompt { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? LastRunAt { get; init; }
        public DateTimeOffset? NextRunAt { get; init; }
    }

    private sealed class DeletedJobPayload
    {
        public required string Id { get; init; }
    }

    private sealed class ExecutionPayload
    {
        public required string Id { get; init; }
        public required string JobId { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public bool Success { get; init; }
        public string? Output { get; init; }
    }
}
