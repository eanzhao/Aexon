using Aexon.Core.Messages;
using Aexon.Core.Storage;

namespace Aexon.Core.Cron;

/// <summary>
/// Persists cron runtime state into the conversation transcript.
/// </summary>
public sealed class PersistentCronRuntime : ICronRuntime
{
    private readonly InMemoryCronRuntime _inner;
    private readonly IConversationJournal _journal;
    private Func<ConversationMessage, CancellationToken, Task>? _messageSink;

    private PersistentCronRuntime(
        InMemoryCronRuntime inner,
        IConversationJournal journal)
    {
        _inner = inner;
        _journal = journal;
    }

    public static Task<PersistentCronRuntime> CreateAsync(
        IConversationJournal journal,
        IReadOnlyList<TranscriptMetadataEntry>? metadataEntries = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var restored = CronPersistence.Restore(metadataEntries ?? []);
        var runtime = new PersistentCronRuntime(
            new InMemoryCronRuntime(restored.Jobs, restored.History),
            journal);
        return Task.FromResult(runtime);
    }

    public CronJob CreateJob(
        string id,
        string schedule,
        string command,
        string? description = null)
    {
        var job = _inner.CreateJob(id, schedule, command, description);
        Persist(CronPersistence.CreateJobEntry(job));
        return job;
    }

    public CronJob CreateWakeupJob(
        string sessionId,
        TimeSpan delay,
        string prompt,
        string reason)
    {
        var job = _inner.CreateWakeupJob(sessionId, delay, prompt, reason);
        Persist(CronPersistence.CreateJobEntry(job));
        return job;
    }

    public CronJob? GetJob(string id) => _inner.GetJob(id);

    public IReadOnlyList<CronJob> ListJobs() => _inner.ListJobs();

    public void SetMessageSink(Func<ConversationMessage, CancellationToken, Task>? messageSink) =>
        _messageSink = messageSink;

    public bool DeleteJob(string id)
    {
        var deleted = _inner.DeleteJob(id);
        if (deleted)
            Persist(CronPersistence.CreateJobDeletedEntry(id.Trim()));
        return deleted;
    }

    public void RecordExecution(CronExecutionRecord record)
    {
        _inner.RecordExecution(record);
        Persist(CronPersistence.CreateExecutionEntry(record));

        if (_inner.GetJob(record.JobId) is { } job)
        {
            Persist(CronPersistence.CreateJobEntry(job));
            EmitScheduledTaskFireMessage(record, job);
        }
    }

    public IReadOnlyList<CronExecutionRecord> ListHistory(
        string? jobId = null,
        int maxResults = 20) =>
        _inner.ListHistory(jobId, maxResults);

    private void Persist(TranscriptMetadataEntry entry)
    {
        try
        {
            _journal.AppendMetadataEntryAsync(entry).GetAwaiter().GetResult();
        }
        catch
        {
            // Cron persistence is best-effort; in-memory state still updates.
        }
    }

    private void EmitScheduledTaskFireMessage(CronExecutionRecord record, CronJob job)
    {
        if (_messageSink == null)
            return;

        var message = new SystemScheduledTaskFireMessage
        {
            Content = BuildScheduledTaskContent(job),
            TaskName = job.Id,
            ScheduledAt = record.StartedAt,
            FiredAt = record.StartedAt,
            Result = record.Success ? "completed" : "failed",
            SessionId = job.SessionId,
            Prompt = job.Prompt,
            Reason = job.Description,
        };

        try
        {
            _messageSink(message, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Runtime messages are best-effort; cron execution should still succeed.
        }
    }

    private static string BuildScheduledTaskContent(CronJob job)
    {
        if (job.Kind != CronJobKind.Wakeup || string.IsNullOrWhiteSpace(job.Prompt))
            return $"Scheduled task '{job.Id}' fired.";

        if (string.IsNullOrWhiteSpace(job.Description))
            return $"Scheduled wakeup '{job.Id}' fired. Prompt: {job.Prompt}";

        return $"Scheduled wakeup '{job.Id}' fired. Reason: {job.Description}. Prompt: {job.Prompt}";
    }
}
