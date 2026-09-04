using System.Runtime.CompilerServices;
using Aexon.Core.Hooks;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;

namespace Aexon.Core.Tools;

/// <summary>
/// Represents streaming tool executor.
/// </summary>
public sealed class StreamingToolExecutor : IToolRuntime
{
    private readonly ToolRegistry _registry;
    private readonly IPermissionChecker _permissions;
    private readonly IHookRuntime _hooks;

    public StreamingToolExecutor(
        ToolRegistry registry,
        IPermissionChecker permissions,
        IHookRuntime? hooks = null)
    {
        _registry = registry;
        _permissions = permissions;
        _hooks = hooks ?? HookRuntime.Empty;
    }

    public async IAsyncEnumerable<ToolRunUpdate> RunBatchAsync(
        IReadOnlyList<ToolUseBlock> invocations,
        ToolExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var concurrentCandidates = new List<ToolExecutionCandidate>();
        var sequentialCandidates = new List<ToolExecutionCandidate>();

        foreach (var invocation in invocations)
        {
            var tool = _registry.Get(invocation.Name);
            if (tool == null)
            {
                yield return CreateCompleted(
                    invocation,
                    UnknownTool.Instance,
                    ToolResult.Error($"Unknown tool: {invocation.Name}"));
                continue;
            }

            if (!context.Tools.Contains(tool))
            {
                yield return CreateCompleted(
                    invocation,
                    tool,
                    ToolResult.Error($"Tool {invocation.Name} is not available in the current mode."));
                continue;
            }

            var validation = await tool.ValidateInputAsync(invocation.Input, context);
            if (!validation.IsValid)
            {
                yield return CreateCompleted(
                    invocation,
                    tool,
                    ToolResult.Error(validation.Message ?? "Invalid input"));
                continue;
            }

            var candidate = new ToolExecutionCandidate(invocation, tool);
            if (tool.IsConcurrencySafe(invocation.Input))
                concurrentCandidates.Add(candidate);
            else
                sequentialCandidates.Add(candidate);
        }

        var concurrentPlans = new List<ToolExecutionPlan>();
        foreach (var candidate in concurrentCandidates)
        {
            var plan = await AuthorizeAsync(candidate, context, cancellationToken);
            if (plan is ToolExecutionPlan authorized)
            {
                concurrentPlans.Add(authorized);
                continue;
            }

            if (plan is ToolPermissionRequestPlan permissionRequest)
            {
                var hookDecision = await _hooks.RunPermissionRequestAsync(
                    new PermissionRequestHookContext(
                        candidate.Invocation,
                        candidate.Tool,
                        context,
                        permissionRequest.ApprovedPlan.EffectiveInput,
                        permissionRequest.Request.Description),
                    cancellationToken);

                if (hookDecision.HasDecision)
                {
                    if (hookDecision.Approved == true)
                    {
                        concurrentPlans.Add(permissionRequest.ApprovedPlan);
                        continue;
                    }

                    yield return CreateCompleted(
                        candidate.Invocation,
                        candidate.Tool,
                        ToolResult.Error(hookDecision.Message ?? "User denied permission"));
                    continue;
                }

                yield return permissionRequest.Request;

                var approved = await permissionRequest.Request.WaitForResponseAsync();
                if (!approved)
                {
                    yield return CreateCompleted(
                        candidate.Invocation,
                        candidate.Tool,
                        ToolResult.Error("User denied permission"));
                    continue;
                }

                RememberApproval(permissionRequest.ApprovedPlan, context);
                concurrentPlans.Add(permissionRequest.ApprovedPlan);
                continue;
            }

            yield return CreateCompleted(
                candidate.Invocation,
                candidate.Tool,
                ToolResult.Error(plan.ErrorMessage ?? "Permission denied"));
        }

        if (concurrentPlans.Count > 0)
        {
            var tasks = concurrentPlans
                .Select(plan => ExecuteAuthorizedAsync(plan, context, cancellationToken))
                .ToArray();

            var outcomes = await Task.WhenAll(tasks);
            foreach (var outcome in outcomes)
                yield return new ToolCompletedUpdate(outcome);
        }

        foreach (var candidate in sequentialCandidates)
        {
            var plan = await AuthorizeAsync(candidate, context, cancellationToken);
            if (plan is ToolPermissionRequestPlan permissionRequest)
            {
                var hookDecision = await _hooks.RunPermissionRequestAsync(
                    new PermissionRequestHookContext(
                        candidate.Invocation,
                        candidate.Tool,
                        context,
                        permissionRequest.ApprovedPlan.EffectiveInput,
                        permissionRequest.Request.Description),
                    cancellationToken);

                if (hookDecision.HasDecision)
                {
                    if (hookDecision.Approved == true)
                    {
                        plan = permissionRequest.ApprovedPlan;
                    }
                    else
                    {
                        yield return CreateCompleted(
                            candidate.Invocation,
                            candidate.Tool,
                            ToolResult.Error(hookDecision.Message ?? "User denied permission"));
                        continue;
                    }
                }
                else
                {
                    yield return permissionRequest.Request;

                    var approved = await permissionRequest.Request.WaitForResponseAsync();
                    if (!approved)
                    {
                        yield return CreateCompleted(
                            candidate.Invocation,
                            candidate.Tool,
                            ToolResult.Error("User denied permission"));
                        continue;
                    }

                    RememberApproval(permissionRequest.ApprovedPlan, context);
                    plan = permissionRequest.ApprovedPlan;
                }
            }

            if (plan is not ToolExecutionPlan authorized)
            {
                yield return CreateCompleted(
                    candidate.Invocation,
                    candidate.Tool,
                    ToolResult.Error(plan.ErrorMessage ?? "Permission denied"));
                continue;
            }

            var outcome = await ExecuteAuthorizedAsync(authorized, context, cancellationToken);
            yield return new ToolCompletedUpdate(outcome);
        }
    }

    private async Task<ToolAuthorizationResult> AuthorizeAsync(
        ToolExecutionCandidate candidate,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preToolUse = await _hooks.RunPreToolUseAsync(
            new PreToolUseHookContext(
                candidate.Invocation,
                candidate.Tool,
                context,
                candidate.Invocation.Input),
            cancellationToken);

        if (preToolUse.Action == HookAction.Block)
        {
            return ToolAuthorizationResult.Denied(
                preToolUse.Message ?? "Blocked by PreToolUse hook");
        }

        var observedInput = preToolUse.UpdatedInput ?? candidate.Invocation.Input;

        var permission = await _permissions.CheckAsync(
            candidate.Tool,
            observedInput,
            context);

        var effectiveInput = permission.UpdatedInput ?? observedInput;

        return permission.Behavior switch
        {
            PermissionBehavior.Allow => new ToolExecutionPlan(
                candidate.Invocation,
                candidate.Tool,
                effectiveInput),
            PermissionBehavior.Ask => new ToolPermissionRequestPlan(
                new ToolPermissionRequestUpdate
                {
                    Invocation = candidate.Invocation,
                    Tool = candidate.Tool,
                    Description = permission.Message ?? candidate.Tool.GetUserFacingName(effectiveInput),
                    ObservedInput = effectiveInput,
                },
                new ToolExecutionPlan(candidate.Invocation, candidate.Tool, effectiveInput)),
            PermissionBehavior.Deny => ToolAuthorizationResult.Denied(
                permission.Message ?? "Permission denied"),
            _ => ToolAuthorizationResult.Denied("Permission denied"),
        };
    }

    private void RememberApproval(
        ToolExecutionPlan plan,
        ToolExecutionContext context)
    {
        if (_permissions is IPermissionApprovalRecorder recorder)
            recorder.RememberApproval(plan.Tool, plan.EffectiveInput, context);
    }

    private async Task<ToolRunOutcome> ExecuteAuthorizedAsync(
        ToolExecutionPlan plan,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ToolResult result;

        try
        {
            result = await plan.Tool.ExecuteAsync(
                plan.EffectiveInput,
                context,
                progress: null,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = ToolResult.Error("Tool execution was cancelled.");
        }
        catch (Exception ex)
        {
            result = ToolResult.Error($"Error: {ex.Message}");
        }

        var outcome = new ToolRunOutcome(
            plan.Invocation,
            plan.Tool,
            ClampResult(plan.Tool, result));

        var postContext = new PostToolUseHookContext(
            plan.Invocation,
            plan.Tool,
            context,
            plan.EffectiveInput,
            outcome.Result);

        await _hooks.OnPostToolUseAsync(postContext, cancellationToken);
        if (outcome.Result.IsError)
            await _hooks.OnPostToolUseFailureAsync(postContext, cancellationToken);

        return outcome;
    }

    private static ToolCompletedUpdate CreateCompleted(
        ToolUseBlock invocation,
        ITool tool,
        ToolResult result) =>
        new(new ToolRunOutcome(invocation, tool, ClampResult(tool, result)));

    private static ToolResult ClampResult(ITool tool, ToolResult result)
    {
        var limit = tool.MaxResultSizeChars;
        if (limit <= 0 || result.Data.Length <= limit)
            return result;

        var marker = $"\n\n[Result truncated to {limit:N0} characters]";
        var payloadLimit = Math.Max(0, limit - marker.Length);
        var truncated = result.Data[..payloadLimit] + marker;

        return new ToolResult
        {
            Data = truncated,
            IsError = result.IsError,
            NewMessages = result.NewMessages,
        };
    }

    private sealed record ToolExecutionCandidate(
        ToolUseBlock Invocation,
        ITool Tool);

    private abstract record ToolAuthorizationResult
    {
        public string? ErrorMessage { get; init; }

        public static ToolAuthorizationResult Denied(string message) =>
            new ToolAuthorizationDenied { ErrorMessage = message };
    }

    private sealed record ToolAuthorizationDenied : ToolAuthorizationResult;

    private sealed record ToolExecutionPlan(
        ToolUseBlock Invocation,
        ITool Tool,
        System.Text.Json.JsonElement EffectiveInput) : ToolAuthorizationResult;

    private sealed record ToolPermissionRequestPlan(
        ToolPermissionRequestUpdate Request,
        ToolExecutionPlan ApprovedPlan) : ToolAuthorizationResult;

    private sealed class UnknownTool : ITool
    {
        public static UnknownTool Instance { get; } = new();

        public string Name => "Unknown";

        public Task<string> GetDescriptionAsync() => Task.FromResult("Unknown tool placeholder");

        public System.Text.Json.JsonElement GetInputSchema() =>
            System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public Task<string> GetPromptAsync(ToolPromptContext context) => Task.FromResult(string.Empty);

        public Task<ToolResult> ExecuteAsync(
            System.Text.Json.JsonElement input,
            ToolExecutionContext context,
            IProgress<ToolProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Error("Unknown tool"));
    }
}
