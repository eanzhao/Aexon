using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aexon.Core.Agents;
using Aexon.Core.Hooks;
using Aexon.Core.Permissions;
using Aexon.Core.Providers;
using Aexon.Core.Query;
using Aexon.Core.Skills;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Represents the input payload for the Agent tool.
/// </summary>
public sealed class AgentToolInput
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; set; }

    [JsonPropertyName("run_in_background")]
    public bool RunInBackground { get; set; }

    [JsonPropertyName("use_isolated_workspace")]
    public bool UseIsolatedWorkspace { get; set; } = true;

    [JsonPropertyName("teammate")]
    public AgentTeammateInput? Teammate { get; set; }
}

/// <summary>
/// Represents teammate-scoped agent execution input.
/// </summary>
public sealed class AgentTeammateInput
{
    [JsonPropertyName("team_name")]
    public string? TeamName { get; set; }

    [JsonPropertyName("member_name")]
    public string? MemberName { get; set; }
}

/// <summary>
/// Runs a read-only subagent for focused codebase research.
/// </summary>
public sealed class AgentTool : ITool
{
    private readonly IAgentExecutionRunner _runner;
    private readonly IProviderCapabilityRouter _providerCapabilityRouter;
    private readonly IAgentTaskRuntime _taskRuntime;
    private readonly IAgentTeamRuntime? _teamRuntime;
    private readonly IAgentMessageRuntime? _messageRuntime;
    private readonly IAgentMessageActivationRuntime? _messageActivationRuntime;
    private readonly IHookRuntime _hooks;
    private readonly BackgroundAgentRunScheduler _backgroundRunScheduler;
    private readonly AgentAutoResumeMode _autoResumeMode;
    private readonly AgentRuntimeOptions? _runtimeOptions;
    private readonly SkillLoader _skillLoader;
    private string? _assignmentErrorMessage;

    public AgentTool(
        IAgentExecutionRunner runner,
        IProviderCapabilityRouter? providerCapabilityRouter = null,
        IAgentTaskRuntime? taskRuntime = null,
        IAgentTeamRuntime? teamRuntime = null,
        IAgentMessageRuntime? messageRuntime = null,
        IHookRuntime? hooks = null,
        BackgroundAgentRunScheduler? backgroundRunScheduler = null,
        IAgentMessageActivationRuntime? messageActivationRuntime = null,
        AgentAutoResumeMode autoResumeMode = AgentAutoResumeMode.Queue,
        AgentRuntimeOptions? runtimeOptions = null,
        SkillLoader? skillLoader = null)
    {
        _runner = runner;
        _providerCapabilityRouter = providerCapabilityRouter ?? new DefaultProviderCapabilityRouter();
        _taskRuntime = taskRuntime ?? new InMemoryAgentTaskRuntime();
        _teamRuntime = teamRuntime;
        _messageRuntime = messageRuntime;
        _messageActivationRuntime = messageActivationRuntime;
        _hooks = hooks ?? HookRuntime.Empty;
        _backgroundRunScheduler = backgroundRunScheduler ?? new BackgroundAgentRunScheduler();
        _autoResumeMode = autoResumeMode;
        _runtimeOptions = runtimeOptions;
        _skillLoader = skillLoader ?? new SkillLoader();
    }

    public string Name => "Agent";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult(
            "Launch a focused read-only subagent that can inspect the codebase and return a concise summary.");

    public JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "The concrete subtask for the subagent to investigate"
            },
            "subagent_type": {
              "type": "string",
              "description": "Optional hint like research, debugging, or review"
            },
            "run_in_background": {
              "type": "boolean",
              "description": "When true, launch the subagent in the background and return immediately"
            },
            "use_isolated_workspace": {
              "type": "boolean",
              "description": "When true, run the subagent in a temporary isolated workspace when possible"
            },
            "teammate": {
              "type": "object",
              "description": "Optional teammate assignment for running as a named team member",
              "properties": {
                "team_name": {
                  "type": "string",
                  "description": "Team id or name"
                },
                "member_name": {
                  "type": "string",
                  "description": "Teammate id or name within the selected team"
                }
              },
              "required": ["team_name", "member_name"],
              "additionalProperties": false
            }
          },
          "required": ["prompt"],
          "additionalProperties": false
        }
        """).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            Launch a focused read-only subagent for bounded research work.

            Good uses:
            - Explore a subsystem and summarize how it works
            - Trace where a behavior is implemented
            - Gather evidence before editing code yourself
            - Review a narrow slice of the codebase and report findings

            Important limits:
            - This Aexon implementation runs subagents in read-only mode
            - The subagent can inspect files and use web discovery tools, but it does not edit files
            - Set run_in_background=true when the work can continue asynchronously
            - By default the subagent runs in a temporary isolated workspace when the repo supports it
            - Use it for separable investigation tasks, not for the final user-facing answer
            - Give it a concrete prompt with a clear scope and expected output
            """);
    }

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
    {
        var parsed = JsonSerializer.Deserialize<AgentToolInput>(input);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Prompt))
            return Task.FromResult(ValidationResult.Invalid("prompt is required."));

        if (parsed.Teammate != null &&
            (string.IsNullOrWhiteSpace(parsed.Teammate.TeamName) ||
             string.IsNullOrWhiteSpace(parsed.Teammate.MemberName)))
        {
            return Task.FromResult(ValidationResult.Invalid("teammate.team_name and teammate.member_name are required."));
        }

        if (parsed.Teammate != null && _teamRuntime == null)
            return Task.FromResult(ValidationResult.Invalid("team runtime is not configured."));

        return Task.FromResult(ValidationResult.Valid());
    }

    public bool IsReadOnly(JsonElement input) => true;

    public bool IsConcurrencySafe(JsonElement input) => true;

    public string GetUserFacingName(JsonElement? input = null) => "Subagent";

    public string? GetActivityDescription(JsonElement? input) => "Running subagent";

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<AgentToolInput>(input);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Prompt))
            return ToolResult.Error("prompt is required.");

        var assignment = ResolveAssignment(parsed);
        if (assignment == null)
            return ToolResult.Error(_assignmentErrorMessage ?? "Failed to resolve agent assignment.");

        var executionContext = AgentExecutionContextSnapshot.Create(context);
        var subagentId = _taskRuntime.AllocateSubagentId();
        var title = BuildTitle(parsed.Prompt);
        var workItem = _taskRuntime.CreateWorkItem(
            title,
            description: parsed.Prompt,
            owner: assignment.Owner,
            subagentId: subagentId);
        _taskRuntime.UpdateWorkItem(workItem.Id, item => item.Status = AgentWorkItemStatus.InProgress);

        if (parsed.RunInBackground)
            return LaunchInBackground(workItem, assignment, parsed, executionContext, subagentId);

        try
        {
            var result = await RunSubagentAsync(
                parsed,
                assignment,
                executionContext,
                workItem.Id,
                cancellationToken);

            if (result.Success)
            {
                AgentWorkItemApprovalCoordinator.TryCompleteSuccessfulRun(_taskRuntime, workItem.Id);
            }
            else
            {
                _taskRuntime.UpdateWorkItem(workItem.Id, item => item.Status = AgentWorkItemStatus.Blocked);
            }

            if (!result.Success)
            {
                return ToolResult.Error(
                    $"{assignment.SubjectLabel} {workItem.Id} failed: {result.ErrorMessage ?? "Unknown error"}");
            }

            return ToolResult.Success(FormatResult(assignment, workItem.Id, parsed, result));
        }
        catch (OperationCanceledException)
        {
            _taskRuntime.UpdateWorkItem(workItem.Id, item => item.Status = AgentWorkItemStatus.Cancelled);
            return ToolResult.Error($"{assignment.SubjectLabel} {workItem.Id} was cancelled.");
        }
        catch (Exception ex)
        {
            _taskRuntime.UpdateWorkItem(workItem.Id, item => item.Status = AgentWorkItemStatus.Blocked);
            return ToolResult.Error($"{assignment.SubjectLabel} {workItem.Id} failed: {ex.Message}");
        }
    }

    private ToolResult LaunchInBackground(
        AgentWorkItem workItem,
        AgentAssignment assignment,
        AgentToolInput input,
        AgentExecutionContextSnapshot executionContext,
        string subagentId)
    {
        try
        {
            RegisterMailboxActivationIfNeeded(input, assignment, executionContext, workItem.Id);
            var backgroundRun = QueueBackgroundRun(workItem, assignment, input, executionContext, subagentId: subagentId);
            return ToolResult.Success(
                $"{assignment.SubjectLabel} {workItem.Id} was queued in the background as {backgroundRun.Id}. " +
                $"Use AgentStatus with id=\"{backgroundRun.Id}\" to inspect progress, or AgentStop to cancel it.");
        }
        catch (Exception ex)
        {
            _taskRuntime.UpdateWorkItem(workItem.Id, item =>
                item.Status = AgentWorkItemStatus.Blocked);
            return ToolResult.Error($"{assignment.SubjectLabel} {workItem.Id} failed to start in the background: {ex.Message}");
        }
    }

    private Task<AgentExecutionResult> RunSubagentAsync(
        AgentToolInput input,
        AgentAssignment assignment,
        AgentExecutionContextSnapshot context,
        string workItemId,
        IProgress<AgentExecutionProgress>? progress,
        CancellationToken cancellationToken,
        AgentMessageActivationRequest? activationRequest = null)
    {
        return _runner.RunAsync(
            new AgentExecutionRequest
            {
                Prompt = input.Prompt.Trim(),
                WorkingDirectory = context.WorkingDirectory,
                Model = context.MainLoopModel,
                Tools = BuildReadOnlyToolRegistry(
                    context.MainLoopModel,
                    context.MainLoopProvider,
                    context.WorkingDirectory,
                    workItemId),
                PermissionContext = ClonePermissionContext(context.PermissionContext),
                UseIsolatedWorkspace = input.UseIsolatedWorkspace,
                Progress = progress,
                SystemPromptAppendix = BuildSystemPromptAppendix(input.SubagentType, assignment, activationRequest),
                Hooks = _hooks,
            },
            cancellationToken);
    }

    private Task<AgentExecutionResult> RunSubagentAsync(
        AgentToolInput input,
        AgentAssignment assignment,
        AgentExecutionContextSnapshot context,
        string workItemId,
        CancellationToken cancellationToken) =>
        RunSubagentAsync(input, assignment, context, workItemId, progress: null, cancellationToken);

    private AgentBackgroundRun QueueBackgroundRun(
        AgentWorkItem workItem,
        AgentAssignment assignment,
        AgentToolInput input,
        AgentExecutionContextSnapshot executionContext,
        AgentMessageActivationRequest? activationRequest = null,
        string? subagentId = null)
    {
        var backgroundRun = _taskRuntime.StartBackgroundRun(
            BuildTitle(input.Prompt),
            owner: assignment.Owner,
            workItemId: workItem.Id,
            initialStatus: AgentBackgroundRunStatus.Queued,
            subagentId: subagentId ?? workItem.SubagentId);
        _taskRuntime.AppendBackgroundRunOutput(
            backgroundRun.Id,
            $"Queued prompt: {input.Prompt.Trim()}");
        AppendResumeTriggerOutput(backgroundRun.Id, activationRequest);

        var cancellationSource = new CancellationTokenSource();
        var logger = new BackgroundRunLogger(_taskRuntime, backgroundRun.Id);
        Action? cancellationCallback = null;
        _taskRuntime.RegisterBackgroundRunCancellation(
            backgroundRun.Id,
            () => cancellationCallback?.Invoke());

        try
        {
            cancellationCallback = _backgroundRunScheduler.Enqueue(
                backgroundRun.Id,
                async cancellationToken =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var result = await RunSubagentAsync(
                            input,
                            assignment,
                            executionContext,
                            workItem.Id,
                            logger,
                            cancellationToken,
                            activationRequest);
                        logger.Flush();
                        if (result.Success)
                        {
                            AgentWorkItemApprovalCoordinator.TryCompleteSuccessfulRun(_taskRuntime, workItem.Id);
                            _taskRuntime.AppendBackgroundRunOutput(
                                backgroundRun.Id,
                                FormatResult(assignment, workItem.Id, input, result));
                            _taskRuntime.StopBackgroundRun(backgroundRun.Id,
                                AgentTerminationInfo.Completed("completed"));
                            await TryAutoResumeAwaitingWorkItemsAsync(
                                assignment.Owner,
                                backgroundRun.Id,
                                cancellationToken);
                            return;
                        }

                        var error = result.ErrorMessage ?? "Unknown error";
                        _taskRuntime.UpdateWorkItem(workItem.Id, item =>
                            item.Status = AgentWorkItemStatus.Blocked);
                        _taskRuntime.AppendBackgroundRunOutput(
                            backgroundRun.Id,
                            $"{assignment.SubjectLabel} {workItem.Id} failed: {error}");
                        _taskRuntime.FailBackgroundRun(backgroundRun.Id,
                            AgentTerminationInfo.Failed(error));
                        await TryAutoResumeAwaitingWorkItemsAsync(
                            assignment.Owner,
                            backgroundRun.Id,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.Flush();
                        _taskRuntime.UpdateWorkItem(workItem.Id, item =>
                            item.Status = AgentWorkItemStatus.Cancelled);
                        _taskRuntime.AppendBackgroundRunOutput(
                            backgroundRun.Id,
                            $"{assignment.SubjectLabel} {workItem.Id} was cancelled.");
                        _taskRuntime.CancelBackgroundRun(backgroundRun.Id,
                            AgentTerminationInfo.Cancelled(source: AgentTerminationSource.Agent));
                        await TryAutoResumeAwaitingWorkItemsAsync(
                            assignment.Owner,
                            backgroundRun.Id,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.Flush();
                        _taskRuntime.UpdateWorkItem(workItem.Id, item =>
                            item.Status = AgentWorkItemStatus.Blocked);
                        _taskRuntime.AppendBackgroundRunOutput(
                            backgroundRun.Id,
                            $"{assignment.SubjectLabel} {workItem.Id} failed: {ex.Message}");
                        _taskRuntime.FailBackgroundRun(backgroundRun.Id,
                            AgentTerminationInfo.Failed(ex.Message));
                        await TryAutoResumeAwaitingWorkItemsAsync(
                            assignment.Owner,
                            backgroundRun.Id,
                            cancellationToken);
                    }
                    finally
                    {
                        cancellationSource.Dispose();
                    }
                },
                onStarted: () =>
                {
                    _taskRuntime.UpdateBackgroundRun(
                        backgroundRun.Id,
                        run => run.Status = AgentBackgroundRunStatus.Running);
                    _taskRuntime.AppendBackgroundRunOutput(
                        backgroundRun.Id,
                        "[status] Background run started.");
                },
                onCancelledWhileQueued: () =>
                {
                    logger.Flush();
                    _taskRuntime.UpdateWorkItem(workItem.Id, item =>
                        item.Status = AgentWorkItemStatus.Cancelled);
                    _taskRuntime.AppendBackgroundRunOutput(
                        backgroundRun.Id,
                        $"{assignment.SubjectLabel} {workItem.Id} was cancelled before execution started.");
                    _taskRuntime.CancelBackgroundRun(backgroundRun.Id,
                        AgentTerminationInfo.Cancelled(
                            "cancelled before execution started",
                            AgentTerminationSource.Scheduler));
                    cancellationSource.Dispose();
                });

            return backgroundRun;
        }
        catch
        {
            cancellationSource.Dispose();
            _taskRuntime.AppendBackgroundRunOutput(
                backgroundRun.Id,
                $"{assignment.SubjectLabel} {workItem.Id} failed to start.");
            _taskRuntime.FailBackgroundRun(backgroundRun.Id,
                AgentTerminationInfo.Failed("failed to start", AgentTerminationSource.System));
            throw;
        }
    }

    private ToolRegistry BuildReadOnlyToolRegistry(
        string model,
        AiProvider provider,
        string workingDirectory,
        string workItemId)
    {
        var registry = new ToolRegistry();
        registry.Register(new FileReadTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new SkillTool(_skillLoader, workingDirectory));
        if (_teamRuntime != null)
            registry.Register(new TeamStatusTool(_teamRuntime));
        if (_messageRuntime != null)
        {
            registry.Register(new MailboxStatusTool(_messageRuntime));
            registry.Register(new MailboxRespondTool(_messageRuntime, _messageActivationRuntime, _taskRuntime));
            registry.Register(new SendMessageTool(
                _messageRuntime,
                _teamRuntime,
                _messageActivationRuntime,
                _taskRuntime,
                workItemId));
        }
        registry.Register(new WebFetchTool());
        registry.Register(new WebSearchTool(
            _providerCapabilityRouter,
            () => model,
            () => provider));
        return registry;
    }

    private void RegisterMailboxActivationIfNeeded(
        AgentToolInput input,
        AgentAssignment assignment,
        AgentExecutionContextSnapshot executionContext,
        string workItemId)
    {
        if (!input.RunInBackground ||
            _messageActivationRuntime == null ||
            string.IsNullOrWhiteSpace(assignment.Owner))
        {
            return;
        }

        var capturedInput = CloneInput(input);
        var capturedAssignment = assignment;
        var capturedContext = executionContext;

        _messageActivationRuntime.RegisterOwner(
            assignment.Owner,
            (request, cancellationToken) => ReactivateFromMailboxAsync(
                capturedInput,
                capturedAssignment,
                capturedContext,
                workItemId,
                request,
                cancellationToken));
    }

    private Task<AgentMessageActivationResult> ReactivateFromMailboxAsync(
        AgentToolInput input,
        AgentAssignment assignment,
        AgentExecutionContextSnapshot executionContext,
        string originalWorkItemId,
        AgentMessageActivationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (HasActiveBackgroundRun(assignment.Owner))
        {
            return Task.FromResult(AgentMessageActivationResult.AlreadyActive(
                assignment.Owner,
                $"Trigger {request.Message.Id} is waiting because a queued or running background run already exists."));
        }

        var reusedWorkItem = TryReuseWorkItemForReactivation(
            originalWorkItemId,
            assignment);
        var workItem = reusedWorkItem ?? _taskRuntime.CreateWorkItem(
            BuildTitle(input.Prompt),
            description: input.Prompt,
            owner: assignment.Owner);
        _taskRuntime.UpdateWorkItem(workItem.Id, item =>
        {
            item.Title = BuildTitle(input.Prompt);
            item.Description = input.Prompt;
            item.Owner = assignment.Owner;
        });
        AgentWorkItemApprovalCoordinator.TryResumeApprovedWorkItem(_taskRuntime, workItem.Id);

        try
        {
            var backgroundRun = QueueBackgroundRun(
                workItem,
                assignment,
                input,
                executionContext,
                request);
            return Task.FromResult(AgentMessageActivationResult.Reactivated(
                assignment.Owner,
                backgroundRun.Id,
                workItem.Id,
                reusedWorkItem == null
                    ? $"Triggered by {request.Message.Id} in {request.Message.ThreadId}."
                    : $"Triggered by {request.Message.Id} in {request.Message.ThreadId}. Reused {workItem.Id}."));
        }
        catch (Exception ex)
        {
            _taskRuntime.UpdateWorkItem(workItem.Id, item => item.Status = AgentWorkItemStatus.Blocked);
            return Task.FromResult(AgentMessageActivationResult.Failed(assignment.Owner, ex.Message));
        }
    }

    private AgentWorkItem? TryReuseWorkItemForReactivation(
        string originalWorkItemId,
        AgentAssignment assignment)
    {
        if (string.IsNullOrWhiteSpace(originalWorkItemId))
            return null;

        var workItem = _taskRuntime.GetWorkItem(originalWorkItemId);
        if (workItem == null ||
            !string.Equals(workItem.Owner, assignment.Owner, StringComparison.OrdinalIgnoreCase) ||
            workItem.Status == AgentWorkItemStatus.Cancelled)
        {
            return null;
        }

        return workItem;
    }

    private bool HasActiveBackgroundRun(string owner)
    {
        return _taskRuntime.ListBackgroundRuns().Any(run =>
            string.Equals(run.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            (run.Status is AgentBackgroundRunStatus.Queued or
                AgentBackgroundRunStatus.Running or
                AgentBackgroundRunStatus.CancellationRequested));
    }

    private static AgentToolInput CloneInput(AgentToolInput input) =>
        new()
        {
            Prompt = input.Prompt,
            SubagentType = input.SubagentType,
            RunInBackground = input.RunInBackground,
            UseIsolatedWorkspace = input.UseIsolatedWorkspace,
            Teammate = input.Teammate == null
                ? null
                : new AgentTeammateInput
                {
                    TeamName = input.Teammate.TeamName,
                    MemberName = input.Teammate.MemberName,
                },
        };

    private async Task TryAutoResumeAwaitingWorkItemsAsync(
        string? owner,
        string backgroundRunId,
        CancellationToken cancellationToken)
    {
        if (_messageRuntime == null ||
            _messageActivationRuntime == null ||
            string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        var results = await AgentAutoResumePolicy.TryResumeEligibleAsync(
            _taskRuntime,
            _messageRuntime,
            _messageActivationRuntime,
            _runtimeOptions?.AutoResumeMode ?? _autoResumeMode,
            owner,
            limit: 1,
            cancellationToken);
        foreach (var result in results)
        {
            foreach (var line in AgentWorkItemResumeFormatter.Format(result)
                         .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _taskRuntime.AppendBackgroundRunOutput(backgroundRunId, $"[auto-resume] {line}");
            }
        }
    }

    private static PermissionContext ClonePermissionContext(PermissionContext source)
    {
        var clone = new PermissionContext
        {
            Mode = source.Mode,
            WorkingDirectory = source.WorkingDirectory,
        };

        foreach (var directory in source.AdditionalWorkingDirectories)
            clone.AdditionalWorkingDirectories.Add(directory);
        foreach (var rule in source.AlwaysAllowRules)
            clone.AlwaysAllowRules.Add(rule);
        foreach (var rule in source.AlwaysAskRules)
            clone.AlwaysAskRules.Add(rule);
        foreach (var rule in source.AlwaysDenyRules)
            clone.AlwaysDenyRules.Add(rule);
        foreach (var rule in source.Rules)
            clone.Rules.Add(rule);

        return clone;
    }

    private void AppendResumeTriggerOutput(
        string backgroundRunId,
        AgentMessageActivationRequest? activationRequest)
    {
        if (activationRequest == null)
            return;

        var message = activationRequest.Message;
        var resumeLine = new StringBuilder();
        resumeLine.Append($"[resume] Triggered by {message.Id} in {message.ThreadId} from {message.From}.");
        if (!string.IsNullOrWhiteSpace(message.Protocol?.ActionName))
            resumeLine.Append($" action={message.Protocol.ActionName}.");
        if (!string.IsNullOrWhiteSpace(activationRequest.ResumeReason))
            resumeLine.Append($" reason={activationRequest.ResumeReason}.");

        _taskRuntime.AppendBackgroundRunOutput(backgroundRunId, resumeLine.ToString());
    }

    private string BuildSystemPromptAppendix(
        string? subagentType,
        AgentAssignment assignment,
        AgentMessageActivationRequest? activationRequest = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BuildSubagentSystemPrompt(subagentType, assignment));

        var resumeAppendix = BuildResumeAppendix(activationRequest);
        if (!string.IsNullOrWhiteSpace(resumeAppendix))
        {
            builder.AppendLine();
            builder.AppendLine(resumeAppendix);
        }

        var mailboxAppendix = BuildMailboxAppendix(assignment);
        if (!string.IsNullOrWhiteSpace(mailboxAppendix))
        {
            builder.AppendLine();
            builder.AppendLine(mailboxAppendix);
        }

        return builder.ToString().TrimEnd();
    }

    private static string? BuildResumeAppendix(AgentMessageActivationRequest? activationRequest)
    {
        if (activationRequest == null)
            return null;

        var message = activationRequest.Message;
        var builder = new StringBuilder();
        builder.AppendLine("Resume trigger:");
        builder.AppendLine($"- Trigger message: {message.Id}");
        builder.AppendLine($"- Thread: {message.ThreadId}");
        builder.AppendLine($"- From: {message.From}");
        builder.AppendLine($"- Kind: {message.Kind}");
        if (!string.IsNullOrWhiteSpace(message.Protocol?.ActionName))
            builder.AppendLine($"- Action: {message.Protocol.ActionName}");
        if (!string.IsNullOrWhiteSpace(activationRequest.ResumeReason))
            builder.AppendLine($"- Resume reason: {activationRequest.ResumeReason}");
        if (message.Protocol?.RequiresResponse == true)
            builder.AppendLine("- A response is expected in this thread.");

        return builder.ToString().TrimEnd();
    }

    private string? BuildMailboxAppendix(AgentAssignment assignment)
    {
        if (_messageRuntime == null || string.IsNullOrWhiteSpace(assignment.Owner))
            return null;

        var unread = _messageRuntime.ListMessages(new AgentMessageListOptions
        {
            Recipient = assignment.Owner,
            Status = AgentMessageStatus.Delivered,
            Limit = 5,
        });

        if (unread.Count == 0)
            return null;

        _messageRuntime.MarkRecipientMessagesRead(assignment.Owner);

        var builder = new StringBuilder();
        builder.AppendLine("Unread mailbox messages:");
        foreach (var message in unread.OrderBy(message => message.CreatedAt))
            builder.AppendLine($"- {AgentMessageFormatter.FormatSummaryLine(message)}");

        return builder.ToString().TrimEnd();
    }

    private static string BuildSubagentSystemPrompt(
        string? subagentType,
        AgentAssignment assignment)
    {
        var kind = string.IsNullOrWhiteSpace(subagentType)
            ? "research"
            : subagentType.Trim();

        var builder = new StringBuilder();
        builder.AppendLine(
            $$"""
            You are a subordinate Aexon agent helping another agent.

            Your job:
            - Focus only on the assigned subtask
            - Investigate efficiently and summarize the answer for the parent agent
            - Stay read-only and do not propose file edits unless the prompt explicitly asks for a recommendation
            - Do not ask the user follow-up questions; make reasonable assumptions and keep moving
            - End with a concise, high-signal summary the parent agent can reuse directly

            Subagent type hint: {{kind}}
            """);

        if (assignment.Team != null &&
            assignment.Member != null)
        {
            builder.AppendLine();
            builder.AppendLine("Team assignment:");
            builder.AppendLine($"- Team: {assignment.Team.Name} ({assignment.Team.Id})");
            builder.AppendLine($"- Teammate: {assignment.Member.Name} ({assignment.Member.Role})");
            builder.AppendLine("- Work only on behalf of this teammate.");
            builder.AppendLine("- Do not speak for the whole team or invent updates from other teammates.");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildTitle(string prompt)
    {
        var singleLine = prompt
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return singleLine.Length <= 72
            ? singleLine
            : singleLine[..69] + "...";
    }

    private static string FormatResult(
        AgentAssignment assignment,
        string workItemId,
        AgentToolInput input,
        AgentExecutionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{assignment.SubjectLabel} {workItemId} completed.");
        builder.AppendLine($"Turns: {result.TurnCount}");
        builder.AppendLine(
            $"Usage: in={result.Usage.InputTokens}, out={result.Usage.OutputTokens}");

        if (!string.IsNullOrWhiteSpace(input.SubagentType))
            builder.AppendLine($"Type: {input.SubagentType.Trim()}");

        builder.AppendLine();
        builder.AppendLine("Summary:");
        builder.AppendLine(result.Summary);

        return builder.ToString().TrimEnd();
    }

    private AgentAssignment? ResolveAssignment(AgentToolInput input)
    {
        _assignmentErrorMessage = null;

        if (input.Teammate == null)
            return AgentAssignment.Subagent();

        if (string.IsNullOrWhiteSpace(input.Teammate.TeamName) ||
            string.IsNullOrWhiteSpace(input.Teammate.MemberName))
        {
            _assignmentErrorMessage = "teammate.team_name and teammate.member_name are required.";
            return null;
        }

        if (_teamRuntime == null)
        {
            _assignmentErrorMessage = "team runtime is not configured.";
            return null;
        }

        var team = AgentTeamLookup.ResolveTeam(_teamRuntime, input.Teammate.TeamName!);
        if (team == null)
        {
            _assignmentErrorMessage = $"Team '{input.Teammate.TeamName!.Trim()}' was not found.";
            return null;
        }

        var teammate = AgentTeamLookup.ResolveMember(team, input.Teammate.MemberName!);
        if (teammate == null)
        {
            _assignmentErrorMessage =
                $"Teammate '{input.Teammate.MemberName!.Trim()}' was not found in team '{team.Name}'.";
            return null;
        }

        return AgentAssignment.ForTeammate(team, teammate);
    }

    private sealed record AgentAssignment(
        string Owner,
        string SubjectLabel,
        AgentTeam? Team = null,
        AgentTeamMember? Member = null)
    {
        public static AgentAssignment Subagent() =>
            new("subagent", "Subagent");

        public static AgentAssignment ForTeammate(
            AgentTeam team,
            AgentTeamMember member) =>
            new($"{team.Name}/{member.Name}", $"Teammate {member.Name}", team, member);
    }

    private sealed record AgentExecutionContextSnapshot(
        string WorkingDirectory,
        string MainLoopModel,
        AiProvider MainLoopProvider,
        PermissionContext PermissionContext)
    {
        public static AgentExecutionContextSnapshot Create(ToolExecutionContext context) =>
            new(
                context.WorkingDirectory,
                context.MainLoopModel,
                context.MainLoopProvider,
                ClonePermissionContext(context.PermissionContext));
    }

    private sealed class BackgroundRunLogger : IProgress<AgentExecutionProgress>
    {
        private readonly IAgentTaskRuntime _taskRuntime;
        private readonly string _backgroundRunId;
        private readonly StringBuilder _textBuffer = new();
        private readonly object _gate = new();

        public BackgroundRunLogger(IAgentTaskRuntime taskRuntime, string backgroundRunId)
        {
            _taskRuntime = taskRuntime;
            _backgroundRunId = backgroundRunId;
        }

        public void Report(AgentExecutionProgress value)
        {
            lock (_gate)
            {
                if (string.Equals(value.Type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    AppendText(value.Message);
                    return;
                }

                FlushCore();
                AppendChunk(FormatProgress(value));
            }
        }

        public void Flush()
        {
            lock (_gate)
                FlushCore();
        }

        private void AppendText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            _textBuffer.Append(text);
            FlushCompletedLines();

            if (_textBuffer.Length >= 240)
                FlushCore();
        }

        private void FlushCompletedLines()
        {
            var current = _textBuffer.ToString();
            var newlineIndex = current.LastIndexOf('\n');
            if (newlineIndex < 0)
                return;

            var completed = current[..(newlineIndex + 1)].TrimEnd();
            _textBuffer.Clear();

            if (newlineIndex + 1 < current.Length)
                _textBuffer.Append(current[(newlineIndex + 1)..]);

            if (!string.IsNullOrWhiteSpace(completed))
                AppendChunk(completed);
        }

        private void FlushCore()
        {
            var text = _textBuffer.ToString().TrimEnd();
            _textBuffer.Clear();

            if (!string.IsNullOrWhiteSpace(text))
                AppendChunk(text);
        }

        private void AppendChunk(string chunk)
        {
            if (!string.IsNullOrWhiteSpace(chunk))
                _taskRuntime.AppendBackgroundRunOutput(_backgroundRunId, chunk);
        }

        private static string FormatProgress(AgentExecutionProgress progress)
        {
            return progress.Type switch
            {
                "tool_start" => $"[{progress.ToolName ?? "tool"}] {progress.Message}",
                "tool_progress" => $"[{progress.ToolName ?? progress.ToolUseId ?? "tool"}] {progress.Message}",
                "tool_result" => progress.IsError
                    ? $"[{progress.ToolName ?? "tool"}] failed\n{progress.Message}".TrimEnd()
                    : $"[{progress.ToolName ?? "tool"}] done",
                "status" => progress.IsError
                    ? $"[error] {progress.Message}"
                    : $"[status] {progress.Message}",
                _ => progress.Message,
            };
        }
    }
}
