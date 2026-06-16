namespace Aexon.Core.Agents;

/// <summary>
/// Defines agent work item status values.
/// </summary>
public enum AgentWorkItemStatus
{
    Pending = 0,
    InProgress = 1,
    Blocked = 2,
    Completed = 3,
    Cancelled = 4,
    AwaitingApproval = 5,
    AwaitingResume = 6,
}

/// <summary>
/// Defines agent background run status values.
/// </summary>
public enum AgentBackgroundRunStatus
{
    Running = 0,
    CancellationRequested = 1,
    Stopped = 2,
    Failed = 3,
    Cancelled = 4,
    Queued = 5,
}

/// <summary>
/// Represents agent work item.
/// </summary>
public sealed class AgentWorkItem
{
    private readonly List<string> _blocks = [];
    private readonly List<string> _blockedBy = [];

    public required string Id { get; init; }
    public string? SubagentId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? Owner { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceId { get; set; }
    public string? SourceThreadId { get; set; }
    public string? ApprovalRequestId { get; set; }
    public string? ApprovalThreadId { get; set; }
    public AgentWorkItemStatus Status { get; set; } = AgentWorkItemStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Blocks => _blocks;
    public IReadOnlyList<string> BlockedBy => _blockedBy;

    public void AddBlock(string taskId)
    {
        if (!_blocks.Contains(taskId, StringComparer.OrdinalIgnoreCase))
            _blocks.Add(taskId);
    }

    public void AddBlockedBy(string taskId)
    {
        if (!_blockedBy.Contains(taskId, StringComparer.OrdinalIgnoreCase))
            _blockedBy.Add(taskId);
    }

    public AgentWorkItem Clone()
    {
        var clone = new AgentWorkItem
        {
            Id = Id,
            SubagentId = SubagentId,
            Title = Title,
            Description = Description,
            Owner = Owner,
            SourceKind = SourceKind,
            SourceId = SourceId,
            SourceThreadId = SourceThreadId,
            ApprovalRequestId = ApprovalRequestId,
            ApprovalThreadId = ApprovalThreadId,
            Status = Status,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };

        foreach (var taskId in _blocks)
            clone.AddBlock(taskId);
        foreach (var taskId in _blockedBy)
            clone.AddBlockedBy(taskId);

        return clone;
    }
}

/// <summary>
/// Represents agent background run.
/// </summary>
public sealed class AgentBackgroundRun
{
    private readonly object _outputGate = new();
    private readonly List<string> _output = [];

    public required string Id { get; init; }
    public string? SubagentId { get; set; }
    public required string Name { get; set; }
    public string? Owner { get; set; }
    public string? WorkItemId { get; set; }
    public AgentBackgroundRunStatus Status { get; set; } = AgentBackgroundRunStatus.Running;
    public string? StopReason { get; set; }
    public AgentTerminationInfo? TerminationInfo { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>
    /// Streamed output entries. Producers append while monitors read these
    /// concurrently, so access is guarded and reads return an immutable
    /// snapshot rather than the live backing list.
    /// </summary>
    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_outputGate)
                return _output.ToArray();
        }
    }

    public void AppendOutput(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk))
            return;

        lock (_outputGate)
            _output.Add(chunk);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Stop(string? reason = null) =>
        Stop(AgentTerminationInfo.Completed(reason));

    public void Stop(AgentTerminationInfo termination)
    {
        Status = AgentBackgroundRunStatus.Stopped;
        StopReason = string.IsNullOrWhiteSpace(termination.Reason) ? StopReason : termination.Reason;
        TerminationInfo = termination;
        UpdatedAt = DateTimeOffset.UtcNow;
        StoppedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(string? reason = null) =>
        Fail(AgentTerminationInfo.Failed(reason));

    public void Fail(AgentTerminationInfo termination)
    {
        Status = AgentBackgroundRunStatus.Failed;
        StopReason = string.IsNullOrWhiteSpace(termination.Reason) ? StopReason : termination.Reason;
        TerminationInfo = termination;
        UpdatedAt = DateTimeOffset.UtcNow;
        StoppedAt = DateTimeOffset.UtcNow;
    }

    public void RequestCancellation(string? reason = null) =>
        RequestCancellation(AgentTerminationInfo.Cancelled(reason));

    public void RequestCancellation(AgentTerminationInfo termination)
    {
        if (Status is AgentBackgroundRunStatus.Stopped or
            AgentBackgroundRunStatus.Failed or
            AgentBackgroundRunStatus.Cancelled)
            return;

        Status = AgentBackgroundRunStatus.CancellationRequested;
        StopReason = string.IsNullOrWhiteSpace(termination.Reason) ? StopReason : termination.Reason;
        TerminationInfo = termination;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Cancel(string? reason = null) =>
        Cancel(AgentTerminationInfo.Cancelled(reason));

    public void Cancel(AgentTerminationInfo termination)
    {
        Status = AgentBackgroundRunStatus.Cancelled;
        StopReason = string.IsNullOrWhiteSpace(termination.Reason) ? StopReason : termination.Reason;
        TerminationInfo = termination;
        UpdatedAt = DateTimeOffset.UtcNow;
        StoppedAt = DateTimeOffset.UtcNow;
    }

    public AgentBackgroundRun Clone()
    {
        var clone = new AgentBackgroundRun
        {
            Id = Id,
            SubagentId = SubagentId,
            Name = Name,
            Owner = Owner,
            WorkItemId = WorkItemId,
            Status = Status,
            StopReason = StopReason,
            TerminationInfo = TerminationInfo,
            StartedAt = StartedAt,
            UpdatedAt = UpdatedAt,
            StoppedAt = StoppedAt,
        };

        foreach (var chunk in Output)
            clone.AppendOutput(chunk);

        clone.UpdatedAt = UpdatedAt;
        clone.StoppedAt = StoppedAt;
        return clone;
    }
}
