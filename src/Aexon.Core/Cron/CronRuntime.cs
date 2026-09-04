namespace Aexon.Core.Cron;

/// <summary>
/// Identifies the execution mode for a scheduled job.
/// </summary>
public enum CronJobKind
{
    Command,
    Wakeup,
}

/// <summary>
/// Represents a scheduled cron job.
/// </summary>
public sealed class CronJob
{
    public CronJobKind Kind { get; init; } = CronJobKind.Command;
    public required string Id { get; init; }
    public required string Schedule { get; init; }
    public required string Command { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public bool RunOnce { get; init; }
    public string? SessionId { get; init; }
    public string? Prompt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }

    public CronJob Clone() =>
        new()
        {
            Kind = Kind,
            Id = Id,
            Schedule = Schedule,
            Command = Command,
            Description = Description,
            Enabled = Enabled,
            RunOnce = RunOnce,
            SessionId = SessionId,
            Prompt = Prompt,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            LastRunAt = LastRunAt,
            NextRunAt = NextRunAt,
        };
}

/// <summary>
/// Represents a single execution of a cron job.
/// </summary>
public sealed class CronExecutionRecord
{
    public required string Id { get; init; }
    public required string JobId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Success { get; set; }
    public string? Output { get; set; }
}

/// <summary>
/// Defines the contract for cron job runtime state.
/// </summary>
public interface ICronRuntime
{
    CronJob CreateJob(string id, string schedule, string command, string? description = null);
    CronJob CreateWakeupJob(string sessionId, TimeSpan delay, string prompt, string reason);
    CronJob? GetJob(string id);
    IReadOnlyList<CronJob> ListJobs();
    bool DeleteJob(string id);
    void RecordExecution(CronExecutionRecord record);
    IReadOnlyList<CronExecutionRecord> ListHistory(string? jobId = null, int maxResults = 20);
}

/// <summary>
/// Provides in-memory cron runtime storage.
/// </summary>
public sealed class InMemoryCronRuntime : ICronRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CronJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CronExecutionRecord> _history = [];

    public InMemoryCronRuntime(
        IEnumerable<CronJob>? jobs = null,
        IEnumerable<CronExecutionRecord>? history = null)
    {
        if (jobs != null)
        {
            foreach (var job in jobs)
                _jobs[job.Id] = job.Clone();
        }

        if (history != null)
            _history.AddRange(history);
    }

    public CronJob CreateJob(
        string id,
        string schedule,
        string command,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var expression = CronExpression.TryParse(schedule)
            ?? throw new ArgumentException($"Invalid cron expression: {schedule}");

        var now = DateTimeOffset.UtcNow;
        var job = new CronJob
        {
            Kind = CronJobKind.Command,
            Id = id.Trim(),
            Schedule = schedule.Trim(),
            Command = command.Trim(),
            Description = NormalizeDescription(description),
            CreatedAt = now,
            UpdatedAt = now,
            NextRunAt = expression.NextOccurrence(now),
        };

        lock (_gate)
        {
            if (_jobs.ContainsKey(job.Id))
                throw new InvalidOperationException($"Cron job '{job.Id}' already exists.");

            _jobs[job.Id] = job;
            return job.Clone();
        }
    }

    public CronJob CreateWakeupJob(
        string sessionId,
        TimeSpan delay,
        string prompt,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), "Wakeup delay must be greater than zero.");

        var now = DateTimeOffset.UtcNow;
        var job = new CronJob
        {
            Kind = CronJobKind.Wakeup,
            Id = CreateWakeupJobId(),
            Schedule = $"@after {(int)Math.Ceiling(delay.TotalSeconds)}s",
            Command = string.Empty,
            Description = NormalizeDescription(reason),
            Enabled = true,
            RunOnce = true,
            SessionId = sessionId.Trim(),
            Prompt = prompt.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            NextRunAt = now.Add(delay),
        };

        lock (_gate)
        {
            if (_jobs.ContainsKey(job.Id))
                throw new InvalidOperationException($"Cron job '{job.Id}' already exists.");

            _jobs[job.Id] = job;
            return job.Clone();
        }
    }

    public CronJob? GetJob(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (_gate)
        {
            return _jobs.TryGetValue(id.Trim(), out var job) ? job.Clone() : null;
        }
    }

    public IReadOnlyList<CronJob> ListJobs()
    {
        lock (_gate)
        {
            return _jobs.Values
                .OrderBy(j => j.CreatedAt)
                .ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase)
                .Select(j => j.Clone())
                .ToArray();
        }
    }

    public bool DeleteJob(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
            return _jobs.Remove(id.Trim());
    }

    public void RecordExecution(CronExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            _history.Add(record);

            if (_jobs.TryGetValue(record.JobId, out var job))
            {
                job.LastRunAt = record.StartedAt;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                if (job.RunOnce)
                {
                    job.Enabled = false;
                    job.NextRunAt = null;
                }
                else
                {
                    var expression = CronExpression.TryParse(job.Schedule);
                    job.NextRunAt = expression?.NextOccurrence(record.StartedAt);
                }
            }
        }
    }

    public IReadOnlyList<CronExecutionRecord> ListHistory(string? jobId = null, int maxResults = 20)
    {
        lock (_gate)
        {
            var query = _history.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(jobId))
                query = query.Where(r => string.Equals(r.JobId, jobId.Trim(), StringComparison.OrdinalIgnoreCase));

            return query
                .OrderByDescending(r => r.StartedAt)
                .Take(maxResults)
                .ToArray();
        }
    }

    private static string? NormalizeDescription(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CreateWakeupJobId() => $"wakeup-{Guid.NewGuid():N}";
}
