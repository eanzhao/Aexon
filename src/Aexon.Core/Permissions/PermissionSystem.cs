using System.Collections.Concurrent;
using System.Text.Json;

namespace Aexon.Core.Permissions;

/// <summary>
/// Defines the permission modes supported by the runtime.
/// </summary>
public enum PermissionMode
{
    /// <summary>
    /// The default mode that asks for approval before write operations.
    /// </summary>
    Default,

    /// <summary>
    /// A planning mode that auto-approves read-only operations.
    /// </summary>
    Plan,

    /// <summary>
    /// An automatic mode that allows operations after safety checks.
    /// </summary>
    Auto,

    /// <summary>
    /// A bypass mode that skips permission checks.
    /// </summary>
    Bypass,
}

/// <summary>
/// Defines the possible outcomes of a permission check.
/// </summary>
public enum PermissionBehavior
{
    /// <summary>
    /// Allows the operation to proceed.
    /// </summary>
    Allow,

    /// <summary>
    /// Denies the operation.
    /// </summary>
    Deny,

    /// <summary>
    /// Requires an explicit approval decision.
    /// </summary>
    Ask,
}

/// <summary>
/// Represents the result of a permission check.
/// </summary>
public record PermissionResult
{
    public required PermissionBehavior Behavior { get; init; }
    public JsonElement? UpdatedInput { get; init; }
    public string? Message { get; init; }

    public static PermissionResult Allow(JsonElement? updatedInput = null) =>
        new() { Behavior = PermissionBehavior.Allow, UpdatedInput = updatedInput };

    public static PermissionResult Deny(string? message = null) =>
        new() { Behavior = PermissionBehavior.Deny, Message = message };

    public static PermissionResult Ask(string? message = null) =>
        new() { Behavior = PermissionBehavior.Ask, Message = message };
}

/// <summary>
/// Stores permission mode and rule sets for the current session.
/// </summary>
public class PermissionContext
{
    public PermissionMode Mode { get; set; } = PermissionMode.Default;

    /// <summary>
    /// Gets or sets the primary working directory.
    /// </summary>
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// Gets additional working directories that are allowed for the session.
    /// </summary>
    public HashSet<string> AdditionalWorkingDirectories { get; } = new();

    /// <summary>
    /// Gets the legacy always-allow rules.
    /// </summary>
    public HashSet<string> AlwaysAllowRules { get; } = new();

    /// <summary>
    /// Gets the legacy always-ask rules.
    /// </summary>
    public HashSet<string> AlwaysAskRules { get; } = new();

    /// <summary>
    /// Gets the legacy always-deny rules.
    /// </summary>
    public HashSet<string> AlwaysDenyRules { get; } = new();

    /// <summary>
    /// Gets the structured permission rules.
    /// </summary>
    public List<PermissionRule> Rules { get; } = new();

    /// <summary>
    /// Gets the scoped tool permission rules.
    /// </summary>
    public List<ToolPermissionRule> ToolRules { get; } = new();

    /// <summary>
    /// Gets the session-scoped permission memory.
    /// </summary>
    public PermissionMemory PermissionMemory { get; } = new();

    public void AddRule(PermissionBehavior behavior, string toolName, string? ruleContent = null)
    {
        Rules.Add(PermissionRule.Create(behavior, toolName, ruleContent));
    }

    public ToolPermissionRule AddRule(
        PermissionBehavior behavior,
        string toolName,
        string scope,
        string pattern)
    {
        var rule = ToolPermissionRule.Create(toolName, scope, pattern, behavior);
        ToolRules.Add(rule);
        return rule;
    }

    public bool RemoveRule(
        PermissionBehavior behavior,
        string toolName,
        string scope,
        string pattern)
    {
        return ToolRules.RemoveAll(rule =>
            rule.Decision == behavior &&
            string.Equals(rule.ToolName, toolName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.Scope, scope, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.Pattern, pattern, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public IEnumerable<PermissionRule> GetRules(PermissionBehavior behavior)
    {
        foreach (var rule in Rules.Where(rule => rule.Behavior == behavior))
            yield return rule;

        IEnumerable<string> legacyRules = behavior switch
        {
            PermissionBehavior.Allow => AlwaysAllowRules,
            PermissionBehavior.Ask => AlwaysAskRules,
            PermissionBehavior.Deny => AlwaysDenyRules,
            _ => Array.Empty<string>(),
        };

        foreach (var legacy in legacyRules)
            yield return PermissionRule.Parse(behavior, legacy);
    }

    public PermissionScopeMatch? ResolveScope(
        Tools.ITool tool,
        JsonElement input,
        string? workingDirectory = null)
    {
        return PermissionScopeResolver.Resolve(
            tool,
            input,
            string.IsNullOrWhiteSpace(workingDirectory)
                ? WorkingDirectory
                : workingDirectory);
    }

    public ToolPermissionRule? MatchRule(
        Tools.ITool tool,
        JsonElement input,
        PermissionBehavior? behavior = null,
        string? workingDirectory = null)
    {
        var scopeMatch = ResolveScope(tool, input, workingDirectory);
        return scopeMatch == null
            ? null
            : MatchRule(tool, scopeMatch, behavior);
    }

    public ToolPermissionRule? MatchRule(
        Tools.ITool tool,
        PermissionScopeMatch scopeMatch,
        PermissionBehavior? behavior = null)
    {
        return ToolPermissionRuleMatcher.FindFirstMatch(
            ToolRules,
            tool,
            scopeMatch.Scope,
            scopeMatch.Target,
            behavior);
    }
}

/// <summary>
/// Represents an in-memory permission cache for the current session.
/// </summary>
public sealed class PermissionMemory
{
    private readonly ConcurrentDictionary<PermissionMemoryKey, byte> _allowed = new();

    public bool IsAllowed(PermissionScopeMatch scopeMatch) =>
        _allowed.ContainsKey(PermissionMemoryKey.From(scopeMatch));

    public void RememberAllowed(PermissionScopeMatch scopeMatch) =>
        _allowed.TryAdd(PermissionMemoryKey.From(scopeMatch), 0);

    public void CopyFrom(PermissionMemory other)
    {
        foreach (var entry in other.Entries)
            _allowed.TryAdd(entry, 0);
    }

    public IReadOnlyCollection<PermissionMemoryKey> Entries => _allowed.Keys.ToArray();
}

/// <summary>
/// Represents a normalized permission-memory key.
/// </summary>
public readonly record struct PermissionMemoryKey(
    string ToolName,
    string Scope,
    string Target)
{
    public static PermissionMemoryKey From(PermissionScopeMatch scopeMatch) =>
        new(
            scopeMatch.ToolName.Trim(),
            scopeMatch.Scope.Trim(),
            scopeMatch.Target.Trim());
}

/// <summary>
/// Defines the contract for permission checkers.
/// </summary>
public interface IPermissionChecker
{
    Task<PermissionResult> CheckAsync(
        Tools.ITool tool,
        JsonElement input,
        Tools.ToolExecutionContext context);
}

/// <summary>
/// Records explicit permission approvals that should be remembered for the session.
/// </summary>
public interface IPermissionApprovalRecorder
{
    void RememberApproval(
        Tools.ITool tool,
        JsonElement input,
        Tools.ToolExecutionContext context);
}

/// <summary>
/// Implements the default permission flow.
/// </summary>
public class DefaultPermissionChecker : IPermissionChecker, IPermissionApprovalRecorder
{
    /// <summary>
    /// Gets or sets the callback used when a tool needs explicit approval.
    /// </summary>
    public Func<Tools.ITool, JsonElement, Task<bool>>? OnPermissionRequest { get; set; }

    public async Task<PermissionResult> CheckAsync(
        Tools.ITool tool,
        JsonElement input,
        Tools.ToolExecutionContext context)
    {
        var permCtx = context.PermissionContext;
        var matchedScopedDenyRule = permCtx.MatchRule(
            tool,
            input,
            PermissionBehavior.Deny,
            context.WorkingDirectory);
        if (matchedScopedDenyRule != null)
            return PermissionResult.Deny($"Denied by rule: {matchedScopedDenyRule.ToExpression()}");

        var matchedDenyRule = PermissionRuleMatcher.FindFirstMatch(
            permCtx.GetRules(PermissionBehavior.Deny),
            tool,
            input);

        // 1. Evaluate deny rules first.
        if (matchedDenyRule != null)
            return PermissionResult.Deny($"Denied by rule: {matchedDenyRule.ToExpression()}");

        // 2. Let the tool perform its own permission checks.
        var toolPermission = await tool.CheckPermissionsAsync(input, context);
        if (toolPermission.Behavior == PermissionBehavior.Deny)
            return toolPermission;

        var effectiveInput = toolPermission.UpdatedInput ?? input;
        var scopeMatch = permCtx.ResolveScope(tool, effectiveInput, context.WorkingDirectory);

        // 3. Bypass mode allows everything.
        if (permCtx.Mode == PermissionMode.Bypass)
            return PermissionResult.Allow(toolPermission.UpdatedInput);

        // 4. Remembered approvals suppress repeat prompts for the same target.
        if (scopeMatch != null && permCtx.PermissionMemory.IsAllowed(scopeMatch))
            return PermissionResult.Allow(toolPermission.UpdatedInput);

        // 5. Evaluate allow rules.
        var matchedScopedAllowRule = scopeMatch == null
            ? null
            : permCtx.MatchRule(tool, scopeMatch, PermissionBehavior.Allow);
        if (matchedScopedAllowRule != null)
            return PermissionResult.Allow(toolPermission.UpdatedInput);

        var matchedAllowRule = PermissionRuleMatcher.FindFirstMatch(
            permCtx.GetRules(PermissionBehavior.Allow),
            tool,
            effectiveInput);
        if (matchedAllowRule != null)
            return PermissionResult.Allow(toolPermission.UpdatedInput);

        // 6. Ask rules must run before read-only and auto-mode shortcuts.
        var matchedScopedAskRule = scopeMatch == null
            ? null
            : permCtx.MatchRule(tool, scopeMatch, PermissionBehavior.Ask);
        if (matchedScopedAskRule != null)
        {
            return PermissionResult.Ask(
                $"Rule requires confirmation: {matchedScopedAskRule.ToExpression()}");
        }

        var matchedAskRule = PermissionRuleMatcher.FindFirstMatch(
            permCtx.GetRules(PermissionBehavior.Ask),
            tool,
            effectiveInput);
        if (matchedAskRule != null)
            return PermissionResult.Ask($"Rule requires confirmation: {matchedAskRule.ToExpression()}");

        // 7. Auto-approve read-only operations in Plan and Auto modes.
        if (tool.IsReadOnly(effectiveInput) &&
            permCtx.Mode is PermissionMode.Plan or PermissionMode.Auto)
            return PermissionResult.Allow(toolPermission.UpdatedInput);

        // 8. Auto mode allows everything else.
        if (permCtx.Mode == PermissionMode.Auto)
            return PermissionResult.Allow(toolPermission.UpdatedInput);

        // 9. Honor explicit allow results returned by the tool.
        if (toolPermission.Behavior == PermissionBehavior.Allow)
            return toolPermission;

        // 10. Fall back to prompting the user.
        return PermissionResult.Ask(
            toolPermission.Message ?? $"Allow {tool.GetUserFacingName(effectiveInput)}?");
    }

    public void RememberApproval(
        Tools.ITool tool,
        JsonElement input,
        Tools.ToolExecutionContext context)
    {
        var scopeMatch = context.PermissionContext.ResolveScope(
            tool,
            input,
            context.WorkingDirectory);
        if (scopeMatch == null)
            return;

        context.PermissionContext.PermissionMemory.RememberAllowed(scopeMatch);
    }
}
