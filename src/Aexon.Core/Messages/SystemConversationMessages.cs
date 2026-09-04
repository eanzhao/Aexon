using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aexon.Core.Messages;

/// <summary>
/// Defines subtype names for structured system messages.
/// </summary>
public static class SystemMessageSubtypes
{
    public const string CompactBoundary = "compact_boundary";
    public const string MicrocompactBoundary = "microcompact_boundary";
    public const string PermissionRetry = "permission_retry";
    public const string StopHookSummary = "stop_hook_summary";
    public const string TurnDuration = "turn_duration";
    public const string LocalCommand = "local_command";
    public const string ApiMetrics = "api_metrics";
    public const string MemorySaved = "memory_saved";
    public const string AgentsKilled = "agents_killed";
    public const string ScheduledTaskFire = "scheduled_task_fire";
    public const string BridgeStatus = "bridge_status";
    public const string Progress = "progress";
    public const string ToolUseSummary = "tool_use_summary";
    public const string Attachment = "attachment";
}

/// <summary>
/// Marks a conversation compaction boundary.
/// </summary>
public record SystemCompactBoundaryMessage : SystemMessage
{
    public SystemCompactBoundaryMessage()
    {
        Subtype = SystemMessageSubtypes.CompactBoundary;
    }

    public string? BoundaryId { get; init; }
    public string? Mode { get; init; }
    public string? Reason { get; init; }
    public bool Automatic { get; init; }
    public int FoldedMessageCount { get; init; }
    public int PreservedMessageCount { get; init; }
    public string? SummaryMessageId { get; init; }
}

/// <summary>
/// Marks a microcompact boundary and the fields that were cleared.
/// </summary>
public record SystemMicrocompactBoundaryMessage : SystemMessage
{
    public SystemMicrocompactBoundaryMessage()
    {
        Subtype = SystemMessageSubtypes.MicrocompactBoundary;
    }

    public string? BoundaryId { get; init; }
    public string? Reason { get; init; }
    public bool Automatic { get; init; }
    public int ClearedToolResultCount { get; init; }
    public int ClearedThinkingBlockCount { get; init; }
}

/// <summary>
/// Describes a permission retry attempt for a tool call.
/// </summary>
public record SystemPermissionRetryMessage : SystemMessage
{
    public SystemPermissionRetryMessage()
    {
        Subtype = SystemMessageSubtypes.PermissionRetry;
    }

    public string? ToolName { get; init; }
    public string? ToolUseId { get; init; }
    public int Attempt { get; init; }
    public string? Reason { get; init; }
    public JsonElement? UpdatedInput { get; init; }
}

/// <summary>
/// Stores a stop-hook execution summary.
/// </summary>
public record SystemStopHookSummaryMessage : SystemMessage
{
    public SystemStopHookSummaryMessage()
    {
        Subtype = SystemMessageSubtypes.StopHookSummary;
    }

    public string? HookEvent { get; init; }
    public bool Success { get; init; }
    public double DurationMs { get; init; }
    public string? Summary { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Stores turn timing information.
/// </summary>
public record SystemTurnDurationMessage : SystemMessage
{
    public SystemTurnDurationMessage()
    {
        Subtype = SystemMessageSubtypes.TurnDuration;
    }

    public int TurnCount { get; init; }
    public double DurationMs { get; init; }
    public string? Model { get; init; }
}

/// <summary>
/// Stores a local command execution record.
/// </summary>
public record SystemLocalCommandMessage : SystemMessage
{
    public SystemLocalCommandMessage()
    {
        Subtype = SystemMessageSubtypes.LocalCommand;
    }

    public string? Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? ExitCode { get; init; }
    public bool IsError { get; init; }
}

/// <summary>
/// Stores API call metrics for a turn.
/// </summary>
public record SystemApiMetricsMessage : SystemMessage
{
    public SystemApiMetricsMessage()
    {
        Subtype = SystemMessageSubtypes.ApiMetrics;
    }

    public string? Model { get; init; }
    public TokenUsage? Usage { get; init; }
    public double? DurationMs { get; init; }
    public string? StopReason { get; init; }
    public bool Success { get; init; }
}

/// <summary>
/// Stores memory save metadata.
/// </summary>
public record SystemMemorySavedMessage : SystemMessage
{
    public SystemMemorySavedMessage()
    {
        Subtype = SystemMessageSubtypes.MemorySaved;
    }

    public string? MemoryKind { get; init; }
    public string? FilePath { get; init; }
    public int? CharacterCount { get; init; }
}

/// <summary>
/// Stores agent termination metadata.
/// </summary>
public record SystemAgentsKilledMessage : SystemMessage
{
    public SystemAgentsKilledMessage()
    {
        Subtype = SystemMessageSubtypes.AgentsKilled;
    }

    public string[] AgentIds { get; init; } = [];
    public string? Reason { get; init; }
}

/// <summary>
/// Stores scheduled task trigger metadata.
/// </summary>
public record SystemScheduledTaskFireMessage : SystemMessage
{
    public SystemScheduledTaskFireMessage()
    {
        Subtype = SystemMessageSubtypes.ScheduledTaskFire;
    }

    public string? TaskName { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public DateTimeOffset? FiredAt { get; init; }
    public string? Result { get; init; }
    public string? SessionId { get; init; }
    public string? Prompt { get; init; }
    public string? Reason { get; init; }
    [JsonIgnore]
    public bool HasWakeupPrompt => !string.IsNullOrWhiteSpace(Prompt);
}

/// <summary>
/// Stores bridge connection state.
/// </summary>
public record SystemBridgeStatusMessage : SystemMessage
{
    public SystemBridgeStatusMessage()
    {
        Subtype = SystemMessageSubtypes.BridgeStatus;
    }

    public string? BridgeName { get; init; }
    public string? Status { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Stores a progress update.
/// </summary>
public record ProgressMessage : SystemMessage
{
    public ProgressMessage()
    {
        Subtype = SystemMessageSubtypes.Progress;
    }

    public string? Stage { get; init; }
    public string? Status { get; init; }
    public double? Percent { get; init; }
}

/// <summary>
/// Stores a completed tool-use summary.
/// </summary>
public record ToolUseSummaryMessage : SystemMessage
{
    public ToolUseSummaryMessage()
    {
        Subtype = SystemMessageSubtypes.ToolUseSummary;
    }

    public string? ToolUseId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
    public string? ResultPreview { get; init; }
}

/// <summary>
/// Stores attachment metadata.
/// </summary>
public record AttachmentMessage : SystemMessage
{
    public AttachmentMessage()
    {
        Subtype = SystemMessageSubtypes.Attachment;
    }

    public string? AttachmentId { get; init; }
    public string? AttachmentName { get; init; }
    public string? MediaType { get; init; }
    public string? SourcePath { get; init; }
    public long? SizeBytes { get; init; }
}

internal static class StructuredSystemMessageCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static JsonElement? Serialize(SystemMessage message)
    {
        var type = message.GetType();
        if (type == typeof(SystemMessage))
            return null;

        return JsonSerializer.SerializeToElement(message, type, SerializerOptions);
    }

    public static SystemMessage Deserialize(
        string id,
        DateTimeOffset timestamp,
        string content,
        string? subtype,
        JsonElement? payload)
    {
        SystemMessage? restored = subtype switch
        {
            SystemMessageSubtypes.CompactBoundary => Deserialize<SystemCompactBoundaryMessage>(payload),
            SystemMessageSubtypes.MicrocompactBoundary => Deserialize<SystemMicrocompactBoundaryMessage>(payload),
            SystemMessageSubtypes.PermissionRetry => Deserialize<SystemPermissionRetryMessage>(payload),
            SystemMessageSubtypes.StopHookSummary => Deserialize<SystemStopHookSummaryMessage>(payload),
            SystemMessageSubtypes.TurnDuration => Deserialize<SystemTurnDurationMessage>(payload),
            SystemMessageSubtypes.LocalCommand => Deserialize<SystemLocalCommandMessage>(payload),
            SystemMessageSubtypes.ApiMetrics => Deserialize<SystemApiMetricsMessage>(payload),
            SystemMessageSubtypes.MemorySaved => Deserialize<SystemMemorySavedMessage>(payload),
            SystemMessageSubtypes.AgentsKilled => Deserialize<SystemAgentsKilledMessage>(payload),
            SystemMessageSubtypes.ScheduledTaskFire => Deserialize<SystemScheduledTaskFireMessage>(payload),
            SystemMessageSubtypes.BridgeStatus => Deserialize<SystemBridgeStatusMessage>(payload),
            SystemMessageSubtypes.Progress => Deserialize<ProgressMessage>(payload),
            SystemMessageSubtypes.ToolUseSummary => Deserialize<ToolUseSummaryMessage>(payload),
            SystemMessageSubtypes.Attachment => Deserialize<AttachmentMessage>(payload),
            _ => null,
        };

        return restored is null
            ? new SystemMessage
            {
                Id = id,
                Timestamp = timestamp,
                Content = content,
                Subtype = subtype,
            }
            : restored with
            {
                Id = id,
                Timestamp = timestamp,
                Content = content,
                Subtype = subtype,
            };
    }

    private static T? Deserialize<T>(JsonElement? payload)
        where T : SystemMessage
    {
        if (payload is not JsonElement element ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>(SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
