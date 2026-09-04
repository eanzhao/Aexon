using System.Text;
using Aexon.Core.Context;
using Aexon.Core.Hooks;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Query;
using Microsoft.Extensions.AI;

namespace Aexon.Core.Agents;

/// <summary>
/// Executes subagent tasks through an isolated QueryEngine instance.
/// </summary>
public sealed class QueryEngineAgentRunner : IAgentExecutionRunner
{
    private readonly IChatClient _chatClient;
    private readonly IPermissionChecker _permissions;
    private readonly IHookRuntime _fallbackHooks;
    private readonly IAgentWorkspaceManager _workspaceManager;

    public QueryEngineAgentRunner(
        IChatClient chatClient,
        IPermissionChecker? permissions = null,
        IHookRuntime? hooks = null,
        IAgentWorkspaceManager? workspaceManager = null)
    {
        _chatClient = chatClient;
        _permissions = permissions ?? new DefaultPermissionChecker();
        _fallbackHooks = hooks ?? HookRuntime.Empty;
        _workspaceManager = workspaceManager ?? new GitWorktreeAgentWorkspaceManager();
    }

    public async Task<AgentExecutionResult> RunAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var workspace = request.UseIsolatedWorkspace
            ? await _workspaceManager.AcquireAsync(request.WorkingDirectory, cancellationToken)
            : AgentWorkspaceLease.Passthrough(
                Path.GetFullPath(request.WorkingDirectory),
                "Workspace isolation disabled.");

        var contextProvider = new ContextProvider
        {
            WorkingDirectory = workspace.WorkingDirectory,
            PermissionContext = ClonePermissionContext(
                request.PermissionContext,
                workspace.WorkingDirectory),
            MemoryContent = request.MemoryContent,
            SessionMemoryContent = request.SessionMemoryContent,
        };

        if (string.IsNullOrWhiteSpace(contextProvider.MemoryContent))
            await contextProvider.LoadMemoryAsync();

        var config = new QueryEngineConfig
        {
            Model = request.Model,
            UseStreamingApi = false,
            MaxTurns = 12,
            EnableAutoCompact = false,
            EnableSessionMemoryCompact = false,
            AppendSystemPrompt = request.SystemPromptAppendix,
        };

        await using var engine = new QueryEngine(
            _chatClient,
            request.Tools,
            _permissions,
            config,
            contextProvider,
            hooks: request.Hooks ?? _fallbackHooks);

        var text = new StringBuilder();
        var sawError = false;
        string? errorMessage = null;
        var turnCount = 0;

        await foreach (var evt in engine.SubmitMessageAsync(request.Prompt, cancellationToken))
        {
            switch (evt)
            {
                case TextDeltaEvent delta:
                    text.Append(delta.Text);
                    request.Progress?.Report(new AgentExecutionProgress("text", delta.Text));
                    break;

                case ToolUseStartEvent toolUse:
                    request.Progress?.Report(new AgentExecutionProgress(
                        "tool_start",
                        SummarizeToolInput(toolUse.Input),
                        toolUse.ToolName,
                        toolUse.ToolUseId));
                    break;

                case ToolProgressEvent toolProgress:
                    request.Progress?.Report(new AgentExecutionProgress(
                        "tool_progress",
                        toolProgress.Message,
                        ToolUseId: toolProgress.ToolUseId));
                    break;

                case ToolResultEvent toolResult:
                    request.Progress?.Report(new AgentExecutionProgress(
                        "tool_result",
                        toolResult.Result,
                        toolResult.ToolName,
                        toolResult.ToolUseId,
                        toolResult.IsError));
                    break;

                case StatusEvent status:
                    request.Progress?.Report(new AgentExecutionProgress("status", status.Status));
                    break;

                case QueryCompleteEvent complete:
                    turnCount = complete.TurnCount;
                    if (!complete.Success)
                    {
                        sawError = true;
                        errorMessage = complete.ErrorMessage;
                        request.Progress?.Report(new AgentExecutionProgress(
                            "status",
                            complete.ErrorMessage ?? "Subagent failed.",
                            IsError: true));
                    }

                    break;
            }
        }

        var summary = text.ToString().Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = engine.Messages
                .OfType<AssistantMessage>()
                .LastOrDefault()?
                .Content
                .OfType<TextBlock>()
                .Select(block => block.Text)
                .FirstOrDefault()
                ?.Trim()
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(summary))
            summary = sawError ? string.Empty : "(Subagent completed without a text summary.)";

        return new AgentExecutionResult(
            Summary: summary,
            Success: !sawError,
            Usage: engine.TotalUsage,
            TurnCount: turnCount == 0 ? 1 : turnCount,
            ErrorMessage: errorMessage);
    }

    private static string SummarizeToolInput(System.Text.Json.JsonElement input)
    {
        var raw = input.GetRawText();
        return raw.Length <= 120 ? raw : $"{raw[..117]}...";
    }

    private static PermissionContext ClonePermissionContext(
        PermissionContext source,
        string workingDirectory)
    {
        var clone = new PermissionContext
        {
            Mode = source.Mode,
            WorkingDirectory = workingDirectory,
        };

        foreach (var path in source.AdditionalWorkingDirectories)
            clone.AdditionalWorkingDirectories.Add(path);

        foreach (var rule in source.AlwaysAllowRules)
            clone.AlwaysAllowRules.Add(rule);

        foreach (var rule in source.AlwaysAskRules)
            clone.AlwaysAskRules.Add(rule);

        foreach (var rule in source.AlwaysDenyRules)
            clone.AlwaysDenyRules.Add(rule);

        foreach (var rule in source.Rules)
            clone.Rules.Add(rule);

        foreach (var rule in source.ToolRules)
            clone.ToolRules.Add(rule);

        clone.PermissionMemory.CopyFrom(source.PermissionMemory);

        return clone;
    }
}
