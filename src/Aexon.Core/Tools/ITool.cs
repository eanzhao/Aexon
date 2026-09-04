using System.Text.Json;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;

namespace Aexon.Core.Tools;

/// <summary>
/// Defines the contract that every tool implementation must follow.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Gets the tool name exposed in tool_use calls.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets backward-compatible aliases for the tool.
    /// </summary>
    string[] Aliases => [];

    /// <summary>
    /// Gets the description shown to the model.
    /// </summary>
    Task<string> GetDescriptionAsync();

    /// <summary>
    /// Gets the JSON schema sent to the API for tool inputs.
    /// </summary>
    JsonElement GetInputSchema();

    /// <summary>
    /// Builds the tool-specific prompt fragment included in the system prompt.
    /// </summary>
    Task<string> GetPromptAsync(ToolPromptContext context);

    /// <summary>
    /// Executes the tool call.
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the input before permission checks run.
    /// </summary>
    Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
        => Task.FromResult(ValidationResult.Valid());

    /// <summary>
    /// Checks whether the tool call should be auto-approved, denied, or confirmed.
    /// </summary>
    Task<PermissionResult> CheckPermissionsAsync(JsonElement input, ToolExecutionContext context)
        => Task.FromResult(PermissionResult.Allow());

    /// <summary>
    /// Gets a value indicating whether the tool is enabled.
    /// </summary>
    bool IsEnabled() => true;

    /// <summary>
    /// Gets a value indicating whether the invocation only reads state.
    /// </summary>
    bool IsReadOnly(JsonElement input) => false;

    /// <summary>
    /// Gets a value indicating whether the invocation can make destructive changes.
    /// </summary>
    bool IsDestructive(JsonElement input) => false;

    /// <summary>
    /// Gets a value indicating whether the invocation can safely run in parallel.
    /// </summary>
    bool IsConcurrencySafe(JsonElement input) => false;

    /// <summary>
    /// Gets the user-facing tool name.
    /// </summary>
    string GetUserFacingName(JsonElement? input = null) => Name;

    /// <summary>
    /// Gets the activity description shown in progress UI.
    /// </summary>
    string? GetActivityDescription(JsonElement? input) => null;

    /// <summary>
    /// Gets the maximum allowed result size in characters.
    /// </summary>
    int MaxResultSizeChars => 100_000;
}

// Context types

/// <summary>
/// Represents a request for interactive user input during tool execution.
/// </summary>
public sealed record UserQuestionRequest(
    string Question,
    IReadOnlyList<string>? Options = null);

/// <summary>
/// Represents the user's answer to an interactive tool prompt.
/// </summary>
public sealed record UserQuestionResponse(string Answer);

/// <summary>
/// Handles user questions raised by tools during execution.
/// </summary>
public delegate Task<UserQuestionResponse> AskUserQuestionHandler(
    UserQuestionRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Represents tool execution context.
/// </summary>
public class ToolExecutionContext
{
    /// <summary>
    /// Gets the working directory.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the permission context.
    /// </summary>
    public required PermissionContext PermissionContext { get; init; }

    /// <summary>
    /// Gets the registered tools.
    /// </summary>
    public required IReadOnlyList<ITool> Tools { get; init; }

    /// <summary>
    /// Gets the current conversation messages.
    /// </summary>
    public required IReadOnlyList<ConversationMessage> Messages { get; init; }

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets a value indicating whether the session is non-interactive.
    /// </summary>
    public bool IsNonInteractiveSession { get; init; }

    /// <summary>
    /// Gets the callback used when a tool needs to ask the user a follow-up question.
    /// </summary>
    public AskUserQuestionHandler? AskUserQuestionAsync { get; init; }

    /// <summary>
    /// Gets the model used by the main loop.
    /// </summary>
    public string MainLoopModel { get; init; } = Query.ClaudeModels.DefaultMainModel;

    /// <summary>
    /// Gets the provider used by the main loop.
    /// </summary>
    public Query.AiProvider MainLoopProvider { get; init; } = Query.AiProvider.Anthropic;

    /// <summary>
    /// Gets the active loop-backed session id, when available.
    /// </summary>
    public string? LoopSessionId { get; init; }
}

/// <summary>
/// Represents tool prompt context.
/// </summary>
public class ToolPromptContext
{
    /// <summary>
    /// Gets the active permission context.
    /// </summary>
    public required PermissionContext PermissionContext { get; init; }

    /// <summary>
    /// Gets the tools currently registered for the session.
    /// </summary>
    public required IReadOnlyList<ITool> Tools { get; init; }
}

// Result types

/// <summary>
/// Represents tool result.
/// </summary>
public class ToolResult
{
    /// <summary>
    /// Gets the text payload returned to the model.
    /// </summary>
    public required string Data { get; init; }

    /// <summary>
    /// Gets a value indicating whether the result represents an error.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    /// Gets additional messages that should be appended to the conversation.
    /// </summary>
    public IReadOnlyList<ConversationMessage>? NewMessages { get; init; }

    public static ToolResult Success(string data) => new() { Data = data };
    public static ToolResult Error(string message) => new() { Data = message, IsError = true };
}

/// <summary>
/// Represents tool progress.
/// </summary>
public record ToolProgress(string ToolUseId, string Type, string? Message = null, double? Percentage = null);

/// <summary>
/// Represents validation result.
/// </summary>
public record ValidationResult(bool IsValid, string? Message = null)
{
    public static ValidationResult Valid() => new(true);
    public static ValidationResult Invalid(string message) => new(false, message);
}
