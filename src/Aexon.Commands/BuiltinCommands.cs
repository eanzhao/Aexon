using System.Text;
using System.Text.Json;
using Aexon.Core.Agents;
using Aexon.Core.Commands;
using Aexon.Core.Compaction;
using Aexon.Core.Permissions;
using Aexon.Core.Query;
using Aexon.Core.Tools;

namespace Aexon.Commands;

/// <summary>
/// Represents help command.
/// </summary>
public class HelpCommand : ICommand
{
    public string Name => "help";
    public string Description => "Show available commands";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        context.WriteLine("\n  Available commands:\n");
        foreach (var cmd in context.Commands)
        {
            var aliases = cmd.Aliases.Length > 0
                ? $" (aliases: {string.Join(", ", cmd.Aliases.Select(a => "/" + a))})"
                : "";
            context.WriteLine($"    /{cmd.Name,-16} {cmd.Description}{aliases}");
        }
        context.WriteLine("");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents clear command.
/// </summary>
public class ClearCommand : ICommand
{
    public string Name => "clear";
    public string Description => "Clear conversation history";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        await context.QueryEngine.ClearMessagesAsync();
        context.RequestClear?.Invoke();
        context.WriteLine("  Conversation cleared.");
    }
}

internal static class GitWorkflowCommandRunner
{
    public static async Task RunPromptInjectionAsync(string prompt, CommandContext context)
    {
        var assistantText = new StringBuilder();

        await foreach (var evt in context.QueryEngine.SubmitMessageAsync(prompt, context.CancellationToken))
        {
            switch (evt)
            {
                case TextDeltaEvent text:
                    assistantText.Append(text.Text);
                    break;

                case ToolUseStartEvent toolUse:
                    FlushAssistantText();
                    context.WriteLine($"[{toolUse.ToolName}] {SummarizeToolInput(toolUse.Input)}");
                    break;

                case PermissionRequestEvent permissionRequest:
                    permissionRequest.SetResponse(true);
                    break;

                case ToolResultEvent toolResult:
                    context.WriteLine($"[{toolResult.ToolName}] {(toolResult.IsError ? "failed" : "done")}");
                    if (toolResult.IsError)
                        WriteMultiline(context, toolResult.Result);
                    break;

                case MessageEndEvent:
                    FlushAssistantText();
                    break;

                case QueryCompleteEvent complete when !complete.Success:
                    FlushAssistantText();
                    context.WriteLine($"Request failed: {complete.ErrorMessage}");
                    break;
            }
        }

        FlushAssistantText();

        void FlushAssistantText()
        {
            if (assistantText.Length == 0)
                return;

            WriteMultiline(context, assistantText.ToString());
            assistantText.Clear();
        }
    }

    public static async Task ExecuteBashAsync(
        string command,
        CommandContext context,
        string? description = null)
    {
        var tool = context.Tools.Get("Bash") ?? context.Tools.Load("Bash");
        if (tool == null)
        {
            context.WriteLine("  Bash tool is not available in this session.");
            return;
        }

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                command,
                description,
            }),
            new ToolExecutionContext
            {
                WorkingDirectory = context.PermissionContext.WorkingDirectory,
                PermissionContext = context.PermissionContext,
                Tools = context.Tools.GetAllTools(),
                Messages = context.QueryEngine.Messages,
                CancellationToken = context.CancellationToken,
                MainLoopModel = context.QueryEngine.CurrentModel,
                MainLoopProvider = context.AiProvider,
            },
            cancellationToken: context.CancellationToken);

        WriteMultiline(context, result.Data);
    }

    public static bool IsSafeBranchName(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return false;

        return branchName.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or '/');
    }

    public static void AppendAdditionalInstructions(StringBuilder builder, string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Additional user instructions:");
        builder.AppendLine(trimmed);
    }

    private static string SummarizeToolInput(JsonElement input)
    {
        var raw = input.GetRawText();
        return raw.Length <= 120 ? raw : $"{raw[..117]}...";
    }

    private static void WriteMultiline(CommandContext context, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var line in normalized.Split('\n'))
            context.WriteLine(line);
    }
}

public sealed class DiffCommand : ICommand
{
    public string Name => "diff";
    public string Description => "Summarize the current git diff";

    public Task ExecuteAsync(string args, CommandContext context) =>
        GitWorkflowCommandRunner.RunPromptInjectionAsync(BuildPrompt(args), context);

    private static string BuildPrompt(string args)
    {
        var builder = new StringBuilder(
            """
            You are handling the /diff slash command inside the current git repository.

            Use Bash for all git inspection. Do not rely on memory and do not ask the user to run the commands manually.

            Required Bash workflow:
            1. Run `git status --short` first.
            2. Run `git diff --stat` and `git diff` to inspect unstaged and mixed working-tree changes.
            3. Check for staged files with `git diff --cached --name-only`.
            4. If staged files exist, also run `git diff --cached --stat` and `git diff --cached`.

            Then produce a concise summary of the current changes:
            - Group the summary by file or logical area.
            - Call out added, deleted, renamed, and modified files explicitly.
            - Distinguish staged versus unstaged changes when that matters.
            - Keep the result brief, concrete, and developer-oriented.
            - If the working tree is clean, say that clearly.
            """);

        GitWorkflowCommandRunner.AppendAdditionalInstructions(builder, args);
        return builder.ToString();
    }
}

public sealed class ReviewCommand : ICommand
{
    public string Name => "review";
    public string Description => "Review the current diff against the base branch";

    public Task ExecuteAsync(string args, CommandContext context) =>
        GitWorkflowCommandRunner.RunPromptInjectionAsync(BuildPrompt(args), context);

    private static string BuildPrompt(string args)
    {
        var builder = new StringBuilder(
            """
            You are handling the /review slash command for the current repository.

            Use Bash for all repository inspection. Review the branch and working tree against the best available base branch.

            Required Bash workflow:
            1. Determine the base branch in this exact preference order: origin/dev, then dev, then main.
               Use git to detect which ref exists, for example with a shell snippet that checks `refs/remotes/origin/dev`, then `refs/heads/dev`, then `refs/heads/main`.
            2. Run `git branch --show-current` and `git status --short`.
            3. Run `git log --oneline <base>..HEAD` to understand branch-only commits.
            4. Run `git diff --stat <base>...HEAD` and `git diff <base>...HEAD` to inspect committed branch changes versus the base branch.
            5. Run `git diff --stat` and `git diff` for unstaged changes.
            6. If `git diff --cached --name-only` returns files, also run `git diff --cached --stat` and `git diff --cached`.

            Review focus:
            - Bugs and behavioral regressions.
            - Style and maintainability problems worth fixing now.
            - SQL safety problems such as string-built queries, missing parameterization, unsafe transactions, or injection risks.
            - Boundary and edge-case concerns, including null handling, empty inputs, paging limits, and error paths.

            Output format:
            - Start with a short summary.
            - Then provide a bulleted list of findings.
            - Each finding must include a severity label and a `file:line` reference derived from the diff hunk when possible.
            - If there are no material findings, say `No findings.` and note any residual testing gaps briefly.
            """);

        GitWorkflowCommandRunner.AppendAdditionalInstructions(builder, args);
        return builder.ToString();
    }
}

public sealed class CommitCommand : ICommand
{
    public string Name => "commit";
    public string Description => "Create a git commit that matches repo conventions";

    public Task ExecuteAsync(string args, CommandContext context) =>
        GitWorkflowCommandRunner.RunPromptInjectionAsync(BuildPrompt(args), context);

    private static string BuildPrompt(string args)
    {
        var builder = new StringBuilder(
            """
            You are handling the /commit slash command inside the current git repository.

            Use Bash for every git command. Execute the commit workflow yourself; do not ask the user to run git manually.

            Required Bash workflow:
            1. Run `git status --short` to see every changed file.
            2. Run `git diff --stat`, `git diff`, and `git diff --cached` so you understand all unstaged and staged changes before committing.
            3. Run `git log --oneline -20` and match the repository's existing commit message convention.
            4. Draft a commit message in that convention before staging.
            5. Stage only the specific files you intend to include by name, for example `git add -- path/to/file1 path/to/file2`.
               Never use `git add -A` and never use `git add .`.
            6. Create the commit with the drafted message.
            7. After the commit, run `git status --short` and `git log -1 --oneline` and report the result.

            Safety requirements:
            - Never use `--no-verify`.
            - Never `--amend` a pushed or published commit.
            - If pre-commit hooks fail, fix the issue and create a new commit rather than amending.
            - If there is nothing to commit, say so clearly and stop.
            - Do not push and do not open a PR as part of /commit.
            """);

        GitWorkflowCommandRunner.AppendAdditionalInstructions(builder, args);
        return builder.ToString();
    }
}

public sealed class BranchCommand : ICommand
{
    public string Name => "branch";
    public string Description => "List branches or create a new branch from the current branch";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            await GitWorkflowCommandRunner.ExecuteBashAsync(
                "git branch",
                context,
                "List local git branches");
            return;
        }

        if (!GitWorkflowCommandRunner.IsSafeBranchName(trimmed))
        {
            context.WriteLine("  Usage: /branch [name]");
            context.WriteLine("  Branch names must be non-empty and may only contain letters, numbers, '.', '_', '-', and '/'.");
            return;
        }

        await GitWorkflowCommandRunner.ExecuteBashAsync(
            $"git checkout -b {trimmed}",
            context,
            $"Create git branch {trimmed}");
    }
}

public sealed class PrCommand : ICommand
{
    public string Name => "pr";
    public string Description => "Draft and create a pull request from the current branch";

    public Task ExecuteAsync(string args, CommandContext context) =>
        GitWorkflowCommandRunner.RunPromptInjectionAsync(BuildPrompt(args), context);

    private static string BuildPrompt(string args)
    {
        var builder = new StringBuilder(
            """
            You are handling the /pr slash command for the current repository.

            Use Bash for every git and GitHub CLI action. Do not ask the user to run the commands manually.

            Required Bash workflow:
            1. Determine the base branch in this exact preference order: origin/dev, then dev, then main.
               Use git to detect which ref exists, for example with a shell snippet that checks `refs/remotes/origin/dev`, then `refs/heads/dev`, then `refs/heads/main`.
            2. Determine the current branch with `git branch --show-current`.
            3. Summarize the branch with `git log --oneline <base>..HEAD`, `git diff --stat <base>...HEAD`, and `git diff <base>...HEAD`.
            4. Draft a PR title under 70 characters.
            5. Draft a PR body with exactly these sections:
               ## Summary
               - 1 to 3 bullets
               ## Test plan
               - [ ] checklist items
            6. Build the PR body with a heredoc, then run `gh pr create --base <base> --head <current> --title ... --body ...`.
               Use a pattern like:
               `title="..."`
               `body=$(cat <<'EOF'`
               `## Summary`
               `- ...`
               `## Test plan`
               `- [ ] ...`
               `EOF`
               `)`
               `gh pr create --base "$base" --head "$current" --title "$title" --body "$body"`

            Safety requirements:
            - Do not force push.
            - Do not target main or master without explicit user confirmation.
            - If only `main` is available as the base branch, stop and ask for confirmation before creating the PR.
            - If the current branch is not on origin yet, a normal `git push -u origin <current>` is allowed before `gh pr create`, but never with `--force`.
            - After creating the PR, report the final title, body, and PR URL.
            """);

        GitWorkflowCommandRunner.AppendAdditionalInstructions(builder, args);
        return builder.ToString();
    }
}

/// <summary>
/// Represents cost command.
/// </summary>
public class CostCommand : ICommand
{
    public string Name => "cost";
    public string Description => "Show token usage and estimated cost";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var usage = context.QueryEngine.TotalUsage;
        var messages = context.QueryEngine.Messages;
        var pricing = ResolvePricing(context.QueryEngine.CurrentModel);

        var inputCost = usage.InputTokens * pricing.InputPerMillion / 1_000_000;
        var outputCost = usage.OutputTokens * pricing.OutputPerMillion / 1_000_000;
        var cacheReadCost = usage.CacheReadInputTokens * pricing.CacheReadPerMillion / 1_000_000;
        var cacheWriteCost = usage.CacheCreationInputTokens * pricing.CacheWritePerMillion / 1_000_000;
        var totalCost = inputCost + outputCost + cacheReadCost + cacheWriteCost;

        context.WriteLine($"""

          Token Usage:
            Input:       {usage.InputTokens,10:N0}  (${inputCost:F4})
            Cache Write: {usage.CacheCreationInputTokens,10:N0}  (${cacheWriteCost:F4})
            Cache Read:  {usage.CacheReadInputTokens,10:N0}  (${cacheReadCost:F4})
            Output:      {usage.OutputTokens,10:N0}  (${outputCost:F4})
            ───────────────────────────
            Input Total: {usage.TotalInputTokens,10:N0}
            Hit Rate:    {usage.CacheHitRate,10:P1}
            Total:       {usage.TotalTokens,10:N0}  (${totalCost:F4})
            Messages:    {messages.Count,10:N0}
        """);
        return Task.CompletedTask;
    }

    private static UsagePricing ResolvePricing(string modelOrAlias)
    {
        var stableId = ClaudeModelCatalog.TryResolve(modelOrAlias)?.StableId;
        return stableId switch
        {
            "claude-haiku-4-5" => new UsagePricing(1.0, 1.25, 0.10, 5.0),
            "claude-3-5-haiku" => new UsagePricing(0.8, 1.0, 0.08, 4.0),
            "claude-opus-4" or "claude-opus-4-1" => new UsagePricing(15.0, 18.75, 1.5, 75.0),
            "claude-opus-4-5" or "claude-opus-4-6" => new UsagePricing(5.0, 6.25, 0.5, 25.0),
            _ => new UsagePricing(3.0, 3.75, 0.3, 15.0),
        };
    }

    private sealed record UsagePricing(
        double InputPerMillion,
        double CacheWritePerMillion,
        double CacheReadPerMillion,
        double OutputPerMillion);
}

/// <summary>
/// Represents exit command.
/// </summary>
public class ExitCommand : ICommand
{
    public string Name => "exit";
    public string Description => "Exit Aexon";
    public string[] Aliases => ["quit", "q"];

    public Task ExecuteAsync(string args, CommandContext context)
    {
        context.RequestExit?.Invoke();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents team command.
/// </summary>
public class TeamCommand : ICommand
{
    private readonly IAgentTeamRuntime? _runtime;

    public TeamCommand(IAgentTeamRuntime? runtime = null)
    {
        _runtime = runtime;
    }

    public string Name => "team";
    public string Description => "Create, inspect, list, or dissolve teams";
    public string[] Aliases => ["teams"];

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var runtime = ResolveRuntime(context);
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Equals("list", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteLine(AgentTeamStatusFormatter.FormatOverview(runtime.ListTeams()));
            return Task.CompletedTask;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var action = parts[0];
        var remainder = parts.Length > 1 ? parts[1] : string.Empty;

        if (action.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return CreateTeamAsync(runtime, remainder, context);
        }

        if (action.Equals("dissolve", StringComparison.OrdinalIgnoreCase))
        {
            return DissolveTeamAsync(runtime, remainder, context);
        }

        if (action.Equals("show", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("inspect", StringComparison.OrdinalIgnoreCase))
        {
            return ShowTeamAsync(runtime, remainder, context);
        }

        var team = AgentTeamLookup.ResolveTeam(runtime, trimmed);
        if (team != null)
        {
            context.WriteLine(AgentTeamStatusFormatter.FormatDetails(team));
            return Task.CompletedTask;
        }

        context.WriteLine("  Usage: /team [list|status], /team create <name> [--lead <name>] [--member <name>]..., /team dissolve <id|name> [reason], /team show <id|name>");
        return Task.CompletedTask;
    }

    private Task CreateTeamAsync(
        IAgentTeamRuntime runtime,
        string args,
        CommandContext context)
    {
        if (!TryParseCreateArguments(args, out var input, out var error))
        {
            context.WriteLine(error ?? "  Usage: /team create <name> [--lead <name>] [--member <name>]...");
            return Task.CompletedTask;
        }

        try
        {
            var team = runtime.CreateTeam(
                input.Name!,
                description: input.Description,
                leadName: input.Lead);

            foreach (var member in input.Members ?? [])
            {
                if (string.IsNullOrWhiteSpace(member) ||
                    string.Equals(member.Trim(), input.Lead?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                runtime.AddMember(team.Id, member);
            }

            team = runtime.GetTeam(team.Id) ?? team;
            context.WriteLine(FormatCreateResult(team));
        }
        catch (Exception ex)
        {
            context.WriteLine($"  {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task DissolveTeamAsync(
        IAgentTeamRuntime runtime,
        string args,
        CommandContext context)
    {
        if (!TryParseTargetAndReason(args, out var target, out var reason, out var error))
        {
            context.WriteLine(error ?? "  Usage: /team dissolve <id|name> [reason]");
            return Task.CompletedTask;
        }

        var team = AgentTeamLookup.ResolveTeam(runtime, target);
        if (team == null)
        {
            context.WriteLine($"  No team matched '{target}'.");
            return Task.CompletedTask;
        }

        runtime.DeleteTeam(team.Id);
        context.WriteLine(FormatDissolveResult(team, reason));
        return Task.CompletedTask;
    }

    private Task ShowTeamAsync(
        IAgentTeamRuntime runtime,
        string args,
        CommandContext context)
    {
        var target = args.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            context.WriteLine(AgentTeamStatusFormatter.FormatOverview(runtime.ListTeams()));
            return Task.CompletedTask;
        }

        var team = AgentTeamLookup.ResolveTeam(runtime, target);
        if (team == null)
        {
            context.WriteLine($"  No team matched '{target}'.");
            return Task.CompletedTask;
        }

        context.WriteLine(AgentTeamStatusFormatter.FormatDetails(team));
        return Task.CompletedTask;
    }

    private IAgentTeamRuntime ResolveRuntime(CommandContext context) =>
        _runtime ?? context.AgentTeamRuntime ?? TeamCommandDefaults.Default;

    private static string FormatCreateResult(AgentTeam team) =>
        $"Team created: {team.Id}\n{AgentTeamStatusFormatter.FormatDetails(team)}";

    private static string FormatDissolveResult(AgentTeam team, string? reason)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Team dissolved: {team.Id}");
        builder.AppendLine($"Team: {team.Name} ({team.Id})");
        builder.AppendLine($"Lead: {FormatLead(team)}");
        builder.AppendLine($"Members: {team.Members.Count}");
        if (!string.IsNullOrWhiteSpace(reason))
            builder.AppendLine($"Reason: {reason.Trim()}");

        return builder.ToString().TrimEnd();
    }

    private static string FormatLead(AgentTeam team)
    {
        if (string.IsNullOrWhiteSpace(team.LeadMemberId))
            return "(none)";

        var lead = team.GetMember(team.LeadMemberId!);
        return lead == null ? team.LeadMemberId! : lead.Name;
    }

    private static bool TryParseCreateArguments(
        string args,
        out TeamCommandCreateInput input,
        out string? error)
    {
        input = new TeamCommandCreateInput();
        error = null;

        var tokens = Tokenize(args);
        if (tokens.Count == 0)
        {
            error = "  team name is required.";
            return false;
        }

        input.Name = tokens[0];
        var members = new List<string>();

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Equals("--lead", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= tokens.Count)
                {
                    error = "  --lead requires a value.";
                    return false;
                }

                input.Lead = tokens[i];
                continue;
            }

            if (token.Equals("--member", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= tokens.Count)
                {
                    error = "  --member requires a value.";
                    return false;
                }

                members.Add(tokens[i]);
                continue;
            }

            if (token.Equals("--description", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= tokens.Count)
                {
                    error = "  --description requires a value.";
                    return false;
                }

                input.Description = string.Join(" ", tokens.Skip(i));
                break;
            }

            members.Add(token);
        }

        input.Members = members.Count == 0 ? [] : members.ToArray();
        return true;
    }

    private static bool TryParseTargetAndReason(
        string args,
        out string target,
        out string? reason,
        out string? error)
    {
        var tokens = Tokenize(args);
        if (tokens.Count == 0)
        {
            target = string.Empty;
            reason = null;
            error = "  team id or name is required.";
            return false;
        }

        target = tokens[0];
        reason = tokens.Count > 1
            ? string.Join(" ", tokens.Skip(1))
            : null;
        error = null;
        return true;
    }

    private static List<string> Tokenize(string args)
    {
        return args.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>
/// Represents the input payload for team creation.
/// </summary>
public sealed class TeamCommandCreateInput
{
    public string? Name { get; set; }
    public string? Lead { get; set; }
    public string? Description { get; set; }
    public string[]? Members { get; set; }
}

internal static class TeamCommandDefaults
{
    public static IAgentTeamRuntime Default { get; } = new InMemoryAgentTeamRuntime();
}

/// <summary>
/// Represents mailbox command.
/// </summary>
public class MailboxCommand : ICommand
{
    private readonly IAgentMessageRuntime? _runtime;

    public MailboxCommand(IAgentMessageRuntime? runtime = null)
    {
        _runtime = runtime;
    }

    public string Name => "mailbox";
    public string Description => "Inspect or acknowledge local agent mailbox messages";
    public string[] Aliases => ["messages"];

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var runtime = ResolveRuntime(context);
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Equals("list", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteLine(AgentMessageFormatter.FormatOverview(
                runtime.ListMessages(new AgentMessageListOptions { Limit = 5 }),
                runtime.GetUnreadCounts()));
            return Task.CompletedTask;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var action = parts[0];
        var remainder = parts.Length > 1 ? parts[1] : string.Empty;

        if (action.Equals("show", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("inspect", StringComparison.OrdinalIgnoreCase))
        {
            return ShowMessageAsync(runtime, remainder, context);
        }

        if (action.Equals("read", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("ack", StringComparison.OrdinalIgnoreCase))
        {
            return ReadMessageAsync(runtime, remainder, context);
        }

        if (action.Equals("for", StringComparison.OrdinalIgnoreCase))
        {
            return ListParticipantMessagesAsync(runtime, remainder, context);
        }

        if (action.Equals("inbox", StringComparison.OrdinalIgnoreCase))
        {
            return ListInboxAsync(runtime, remainder, context);
        }

        if (action.Equals("outbox", StringComparison.OrdinalIgnoreCase))
        {
            return ListOutboxAsync(runtime, remainder, context);
        }

        if (action.Equals("thread", StringComparison.OrdinalIgnoreCase))
        {
            return ShowThreadAsync(runtime, remainder, context);
        }

        if (action.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            return ShowPendingActionsAsync(runtime, remainder, context);
        }

        if (action.Equals("respond", StringComparison.OrdinalIgnoreCase))
        {
            return RespondToMessageAsync(runtime, remainder, context);
        }

        if (runtime.GetMessage(trimmed) is { } direct)
        {
            context.WriteLine(AgentMessageFormatter.FormatDetails(direct));
            return Task.CompletedTask;
        }

        context.WriteLine("  Usage: /mailbox [list|status], /mailbox show <message-id>, /mailbox read <message-id>, /mailbox for <participant>, /mailbox inbox <participant>, /mailbox outbox <participant>, /mailbox thread <thread-id>, /mailbox pending <participant>, /mailbox respond <message-id> <decision> [note]");
        return Task.CompletedTask;
    }

    private static Task ShowMessageAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var id = args.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            context.WriteLine("  Usage: /mailbox show <message-id>");
            return Task.CompletedTask;
        }

        var message = runtime.GetMessage(id);
        context.WriteLine(message == null
            ? $"  Message '{id}' was not found."
            : AgentMessageFormatter.FormatDetails(message));
        return Task.CompletedTask;
    }

    private static Task ReadMessageAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var id = args.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            context.WriteLine("  Usage: /mailbox read <message-id>");
            return Task.CompletedTask;
        }

        runtime.MarkMessageRead(id);
        var message = runtime.GetMessage(id);
        context.WriteLine(message == null
            ? $"  Message '{id}' was not found."
            : AgentMessageFormatter.FormatDetails(message));
        return Task.CompletedTask;
    }

    private static Task ListParticipantMessagesAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var participant = args.Trim();
        if (string.IsNullOrWhiteSpace(participant))
        {
            context.WriteLine("  Usage: /mailbox for <participant>");
            return Task.CompletedTask;
        }

        var messages = runtime.ListMessages()
            .Where(message =>
                string.Equals(message.From, participant, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message.To, participant, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        context.WriteLine(AgentMessageFormatter.FormatList(messages));
        return Task.CompletedTask;
    }

    private static Task ListInboxAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var participant = args.Trim();
        if (string.IsNullOrWhiteSpace(participant))
        {
            context.WriteLine("  Usage: /mailbox inbox <participant>");
            return Task.CompletedTask;
        }

        var messages = runtime.ListMessages(new AgentMessageListOptions
        {
            Recipient = participant,
        });
        context.WriteLine(AgentMessageFormatter.FormatInbox(participant, messages));
        return Task.CompletedTask;
    }

    private static Task ListOutboxAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var participant = args.Trim();
        if (string.IsNullOrWhiteSpace(participant))
        {
            context.WriteLine("  Usage: /mailbox outbox <participant>");
            return Task.CompletedTask;
        }

        var messages = runtime.ListMessages(new AgentMessageListOptions
        {
            Sender = participant,
        });
        context.WriteLine(AgentMessageFormatter.FormatOutbox(participant, messages));
        return Task.CompletedTask;
    }

    private static Task ShowThreadAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var threadId = args.Trim();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            context.WriteLine("  Usage: /mailbox thread <thread-id>");
            return Task.CompletedTask;
        }

        context.WriteLine(AgentMessageFormatter.FormatThread(threadId, runtime.ListThread(threadId)));
        return Task.CompletedTask;
    }

    private static Task ShowPendingActionsAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var participant = args.Trim();
        if (string.IsNullOrWhiteSpace(participant))
        {
            context.WriteLine("  Usage: /mailbox pending <participant>");
            return Task.CompletedTask;
        }

        context.WriteLine(AgentMessageFormatter.FormatPendingActions(
            participant,
            AgentMessageWorkflow.ListPendingActions(runtime, participant)));
        return Task.CompletedTask;
    }

    private static Task RespondToMessageAsync(
        IAgentMessageRuntime runtime,
        string args,
        CommandContext context)
    {
        var parts = args.Split(' ', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            context.WriteLine("  Usage: /mailbox respond <message-id> <decision> [note]");
            return Task.CompletedTask;
        }

        var trigger = runtime.GetMessage(parts[0]);
        if (trigger == null)
        {
            context.WriteLine($"  Message '{parts[0]}' was not found.");
            return Task.CompletedTask;
        }

        if (!AgentMessageWorkflow.TryBuildResponse(
                trigger,
                trigger.To,
                parts[1],
                parts.Length > 2 ? parts[2] : null,
                out var response,
                out var error))
        {
            context.WriteLine($"  {error}");
            return Task.CompletedTask;
        }

        var delivered = runtime.SendMessage(
            response!.From,
            response.To,
            response.Kind,
            response.Body,
            response.Subject,
            response.RelatedMessageId,
            response.Protocol);
        runtime.MarkMessageRead(trigger.Id);
        AgentMailboxTaskProjector.Synchronize(runtime, context.AgentTaskRuntime);
        context.WriteLine($"Responded to {trigger.Id} with {delivered.Id}.");
        context.WriteLine(AgentMessageFormatter.FormatDetails(delivered));
        return Task.CompletedTask;
    }

    private IAgentMessageRuntime ResolveRuntime(CommandContext context) =>
        _runtime ?? context.AgentMessageRuntime ?? MailboxCommandDefaults.Default;
}

internal static class MailboxCommandDefaults
{
    public static IAgentMessageRuntime Default { get; } = new InMemoryAgentMessageRuntime();
}

/// <summary>
/// Represents agents command.
/// </summary>
public class AgentsCommand : ICommand
{
    public string Name => "agents";
    public string Description => "Show or manage subagent work items and background runs";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            context.WriteLine(AgentStatusFormatter.FormatOverview(context.AgentTaskRuntime));
            return Task.CompletedTask;
        }

        var parts = trimmed.Split(' ', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 &&
            parts[0] is "stop" or "cancel")
        {
            if (parts.Length < 2)
            {
                context.WriteLine("  Usage: /agents stop <background-run-id> [reason]");
                return Task.CompletedTask;
            }

            var id = parts[1];
            var reason = parts.Length > 2 ? parts[2] : null;
            var termination = AgentTerminationInfo.Cancelled(reason, AgentTerminationSource.User);
            var result = context.AgentTaskRuntime.RequestBackgroundRunCancellation(id, termination);

            var message = result switch
            {
                AgentBackgroundRunCancellationResult.Requested =>
                    $"  Cancellation requested for {id}.",
                AgentBackgroundRunCancellationResult.AlreadyRequested =>
                    $"  Cancellation was already requested for {id}.",
                AgentBackgroundRunCancellationResult.AlreadyCompleted =>
                    $"  {id} has already finished.",
                AgentBackgroundRunCancellationResult.Unsupported =>
                    $"  {id} does not support cancellation.",
                _ =>
                    $"  No background run matched id '{id}'.",
            };

            if (result == AgentBackgroundRunCancellationResult.Requested)
                context.AgentTaskRuntime.AppendBackgroundRunOutput(id, $"[status] {message.Trim()}");

            context.WriteLine(message);
            return Task.CompletedTask;
        }

        if (parts.Length > 0 &&
            parts[0] is "prune" or "archive")
        {
            if (!TryParsePruneOptions(trimmed, out var options, out var error))
            {
                context.WriteLine(error ?? "  Invalid /agents prune arguments.");
                context.WriteLine("  Usage: /agents prune [--keep-runs <n>] [--keep-work-items <n>]");
                return Task.CompletedTask;
            }

            var result = context.AgentTaskRuntime.PruneHistory(options);
            var message = result.HasChanges
                ? $"  Pruned {result.RemovedBackgroundRunCount} background run(s) and {result.RemovedWorkItemCount} work item(s)."
                : "  Nothing to prune.";
            context.WriteLine(message);
            return Task.CompletedTask;
        }

        if (parts.Length > 0 &&
            parts[0].Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            return ResumeWorkItemAsync(trimmed, context);
        }

        if (parts.Length > 0 &&
            parts[0].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigureRuntimeAsync(trimmed, context);
        }

        if (parts.Length > 0 &&
            parts[0].Equals("wait", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForBackgroundRunAsync(trimmed, context);
        }

        if (parts.Length > 0 &&
            parts[0].Equals("tail", StringComparison.OrdinalIgnoreCase))
        {
            return TailBackgroundRunAsync(trimmed, context);
        }

        if (parts.Length > 0 &&
            parts[0].Equals("attention", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseAttentionOptions(trimmed, out var owner, out var limit, out var error))
            {
                context.WriteLine(error ?? "  Invalid /agents attention arguments.");
                context.WriteLine("  Usage: /agents attention [--owner <owner>] [--limit <n>]");
                return Task.CompletedTask;
            }

            context.WriteLine(AgentStatusFormatter.FormatAttention(context.AgentTaskRuntime, owner, limit));
            return Task.CompletedTask;
        }

        if (parts.Length > 0 &&
            parts[0].Equals("summary", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseSummaryOptions(trimmed, out var options, out var error))
            {
                context.WriteLine(error ?? "  Invalid /agents summary arguments.");
                context.WriteLine("  Usage: /agents, /agents summary [--owner <owner>] [--recent-limit <n>], /agents attention [--owner <owner>] [--limit <n>], /agents config [auto-resume [queue|latest|disabled]], /agents resume <work-item-id>, /agents prune [--keep-runs <n>] [--keep-work-items <n>], /agents wait [any|all] <background-run-id> [more-ids...] [--timeout-ms <n>] [--poll-ms <n>] [--include-output], /agents <id>, /agents list [--kind <all|work_items|background_runs>] [--status <status>] [--owner <owner>] [--offset <n>] [--limit <n>], /agents stop <background-run-id> [reason]");
                return Task.CompletedTask;
            }

            context.WriteLine(AgentStatusFormatter.FormatSummary(context.AgentTaskRuntime, options));
            return Task.CompletedTask;
        }

        if (trimmed.StartsWith("--", StringComparison.Ordinal) ||
            parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseOverviewOptions(trimmed, out var options, out var error))
            {
                context.WriteLine(error ?? "  Invalid /agents arguments.");
                context.WriteLine("  Usage: /agents, /agents summary [--owner <owner>] [--recent-limit <n>], /agents attention [--owner <owner>] [--limit <n>], /agents config [auto-resume [queue|latest|disabled]], /agents resume <work-item-id>, /agents prune [--keep-runs <n>] [--keep-work-items <n>], /agents wait [any|all] <background-run-id> [more-ids...] [--timeout-ms <n>] [--poll-ms <n>] [--include-output], /agents <id>, /agents list [--kind <all|work_items|background_runs>] [--status <status>] [--owner <owner>] [--offset <n>] [--limit <n>], /agents stop <background-run-id> [reason]");
                return Task.CompletedTask;
            }

            context.WriteLine(AgentStatusFormatter.FormatOverview(context.AgentTaskRuntime, options));
            return Task.CompletedTask;
        }

        if (AgentStatusFormatter.TryFormatDetails(
                context.AgentTaskRuntime,
                trimmed,
                includeOutput: true,
                outputOffset: null,
                outputLimit: null,
                out var details))
        {
            context.WriteLine(details);
            return Task.CompletedTask;
        }

        context.WriteLine(details);
        context.WriteLine("  Usage: /agents, /agents summary [--owner <owner>] [--recent-limit <n>], /agents attention [--owner <owner>] [--limit <n>], /agents config [auto-resume [queue|latest|disabled]], /agents resume <work-item-id>, /agents prune [--keep-runs <n>] [--keep-work-items <n>], /agents wait [any|all] <background-run-id> [more-ids...] [--timeout-ms <n>] [--poll-ms <n>] [--include-output], /agents tail <background-run-id> [--last <n>] [--follow] [--poll-ms <n>], /agents <id>, /agents list [--kind <all|work_items|background_runs>] [--status <status>] [--owner <owner>] [--offset <n>] [--limit <n>], /agents stop <background-run-id> [reason]");
        return Task.CompletedTask;
    }

    private static Task ConfigureRuntimeAsync(
        string args,
        CommandContext context)
    {
        var parts = args.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 || (parts.Length == 2 && parts[1].Equals("show", StringComparison.OrdinalIgnoreCase)))
        {
            WriteRuntimeConfig(context);
            return Task.CompletedTask;
        }

        if (parts.Length == 2 && parts[1].Equals("auto-resume", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteLine($"  Auto-resume: {context.CurrentAgentAutoResumeMode.ToString().ToLowerInvariant()}");
            return Task.CompletedTask;
        }

        if (parts.Length == 3 && parts[1].Equals("auto-resume", StringComparison.OrdinalIgnoreCase))
        {
            if (!AgentAutoResumeModeParser.TryParse(parts[2], out var mode))
            {
                context.WriteLine($"  Unknown auto-resume mode: {parts[2]}");
                context.WriteLine($"  Usage: /agents config auto-resume <{AgentAutoResumeModeParser.Usage}>");
                return Task.CompletedTask;
            }

            if (context.AgentRuntimeOptions == null)
            {
                context.WriteLine("  Agent runtime configuration is not writable in this context.");
                return Task.CompletedTask;
            }

            context.AgentRuntimeOptions.AutoResumeMode = mode;
            context.WriteLine($"  Auto-resume mode set to {mode.ToString().ToLowerInvariant()} for this session.");
            return Task.CompletedTask;
        }

        context.WriteLine("  Usage: /agents config [show], /agents config auto-resume, /agents config auto-resume <queue|latest|disabled>");
        return Task.CompletedTask;
    }

    private static void WriteRuntimeConfig(CommandContext context)
    {
        context.WriteLine("  Agent runtime config:");
        context.WriteLine($"    Auto-resume: {context.CurrentAgentAutoResumeMode.ToString().ToLowerInvariant()}");
        context.WriteLine("    Change with: /agents config auto-resume <queue|latest|disabled>");
    }

    private static async Task ResumeWorkItemAsync(
        string args,
        CommandContext context)
    {
        var parts = args.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            context.WriteLine("  Usage: /agents resume <work-item-id>");
            return;
        }

        if (context.AgentMessageRuntime == null || context.AgentMessageActivationRuntime == null)
        {
            context.WriteLine("  Mailbox resume is not configured in this runtime.");
            return;
        }

        var result = await AgentWorkItemResumer.TryResumeAsync(
            context.AgentTaskRuntime,
            context.AgentMessageRuntime,
            context.AgentMessageActivationRuntime,
            parts[1],
            context.CancellationToken);
        context.WriteLine(AgentWorkItemResumeFormatter.Format(result));
    }

    private static async Task WaitForBackgroundRunAsync(
        string args,
        CommandContext context)
    {
        if (!TryParseWaitOptions(args, out var options, out var error))
        {
            context.WriteLine(error ?? "  Invalid /agents wait arguments.");
            context.WriteLine("  Usage: /agents wait [any|all] <background-run-id> [more-ids...] [--timeout-ms <n>] [--poll-ms <n>] [--include-output]");
            return;
        }

        var waitResult = await AgentBackgroundRunWaiter.WaitManyAsync(
            context.AgentTaskRuntime,
            options.BackgroundRunIds,
            options.WaitMode,
            options.PollInterval,
            options.Timeout,
            context.DelayAsync,
            context.CancellationToken);

        switch (waitResult.Outcome)
        {
            case AgentBackgroundRunWaitOutcome.NotFound:
                context.WriteLine(BuildWaitNotFoundMessage(waitResult));
                return;

            case AgentBackgroundRunWaitOutcome.TimedOut:
                context.WriteLine(BuildWaitTimedOutMessage(options, waitResult));
                return;
        }

        WriteWaitCompletion(context, options, waitResult);
    }

    private static async Task TailBackgroundRunAsync(
        string args,
        CommandContext context)
    {
        if (!TryParseTailOptions(args, out var options, out var error))
        {
            context.WriteLine(error ?? "  Invalid /agents tail arguments.");
            context.WriteLine("  Usage: /agents tail <background-run-id> [--last <n>] [--follow] [--poll-ms <n>]");
            return;
        }

        if (!AgentStatusFormatter.TryGetOutputPage(
                context.AgentTaskRuntime,
                options.BackgroundRunId,
                offset: 0,
                limit: null,
                out var initialRun,
                out var initialPage,
                out var lookupError))
        {
            context.WriteLine($"  {lookupError}");
            return;
        }

        var initialOffset = Math.Max(0, initialPage.TotalCount - options.Last);
        AgentStatusFormatter.TryGetOutputPage(
            context.AgentTaskRuntime,
            options.BackgroundRunId,
            initialOffset,
            options.Last,
            out var run,
            out var page,
            out _);
        context.WriteLine(AgentStatusFormatter.FormatOutputPage(run!, page, includeRunHeader: true));

        if (!options.Follow)
            return;

        var nextOffset = page.NextOffset;
        context.WriteLine(
            $"[tail] Following {options.BackgroundRunId} every {options.PollInterval.TotalMilliseconds:0}ms until it finishes.");

        while (true)
        {
            await DelayAsync(context, options.PollInterval);

            if (!AgentStatusFormatter.TryGetOutputPage(
                    context.AgentTaskRuntime,
                    options.BackgroundRunId,
                    nextOffset,
                    limit: null,
                    out run,
                    out page,
                    out lookupError))
            {
                context.WriteLine($"[tail] {lookupError}");
                return;
            }

            if (page.Entries.Count > 0)
            {
                context.WriteLine(AgentStatusFormatter.FormatOutputPage(run!, page, includeRunHeader: false));
                nextOffset = page.NextOffset;
            }

            if (AgentBackgroundRunWaiter.IsTerminal(run!.Status) && nextOffset >= page.TotalCount)
            {
                context.WriteLine($"[tail] {run.Id} finished with status {run.Status}.");
                return;
            }
        }
    }

    private static bool TryParseOverviewOptions(
        string args,
        out AgentStatusOverviewOptions options,
        out string? error)
    {
        options = new AgentStatusOverviewOptions();
        error = null;

        var tokens = args
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count > 0 &&
            tokens[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        AgentStatusOverviewKind kind = AgentStatusOverviewKind.All;
        string? status = null;
        string? owner = null;
        int offset = 0;
        int? limit = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"  Unknown argument: {token}";
                return false;
            }

            if (i + 1 >= tokens.Count)
            {
                error = $"  Missing value for {token}.";
                return false;
            }

            var value = tokens[++i];
            switch (token)
            {
                case "--kind":
                    if (!AgentStatusFormatter.TryParseOverviewKind(value, out kind))
                    {
                        error = "  --kind must be all, work_items, or background_runs.";
                        return false;
                    }

                    break;

                case "--status":
                    status = value;
                    break;

                case "--owner":
                    owner = value;
                    break;

                case "--offset":
                    if (!int.TryParse(value, out offset) || offset < 0)
                    {
                        error = "  --offset must be a non-negative integer.";
                        return false;
                    }

                    break;

                case "--limit":
                    if (!int.TryParse(value, out var parsedLimit) || parsedLimit <= 0)
                    {
                        error = "  --limit must be a positive integer.";
                        return false;
                    }

                    limit = parsedLimit;
                    break;

                default:
                    error = $"  Unknown option: {token}";
                    return false;
            }
        }

        options = new AgentStatusOverviewOptions
        {
            Kind = kind,
            Status = status,
            Owner = owner,
            Offset = offset,
            Limit = limit,
        };
        return true;
    }

    private static bool TryParseSummaryOptions(
        string args,
        out AgentStatusSummaryOptions options,
        out string? error)
    {
        options = new AgentStatusSummaryOptions();
        error = null;

        var tokens = args
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count > 0 &&
            tokens[0].Equals("summary", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        string? owner = null;
        var recentLimit = 3;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"  Unknown argument: {token}";
                return false;
            }

            if (i + 1 >= tokens.Count)
            {
                error = $"  Missing value for {token}.";
                return false;
            }

            var value = tokens[++i];
            switch (token)
            {
                case "--owner":
                    owner = value;
                    break;

                case "--recent-limit":
                    if (!int.TryParse(value, out recentLimit) || recentLimit <= 0)
                    {
                        error = "  --recent-limit must be a positive integer.";
                        return false;
                    }

                    break;

                default:
                    error = $"  Unknown option: {token}";
                    return false;
            }
        }

        options = new AgentStatusSummaryOptions
        {
            Owner = owner,
            RecentLimit = recentLimit,
        };
        return true;
    }

    private static bool TryParseAttentionOptions(
        string args,
        out string? owner,
        out int? limit,
        out string? error)
    {
        owner = null;
        limit = null;
        error = null;

        var tokens = args
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count > 0 &&
            tokens[0].Equals("attention", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"  Unknown argument: {token}";
                return false;
            }

            if (i + 1 >= tokens.Count)
            {
                error = $"  Missing value for {token}.";
                return false;
            }

            var value = tokens[++i];
            switch (token)
            {
                case "--owner":
                    owner = value;
                    break;
                case "--limit":
                    if (!int.TryParse(value, out var parsedLimit) || parsedLimit <= 0)
                    {
                        error = "  --limit must be a positive integer.";
                        return false;
                    }

                    limit = parsedLimit;
                    break;
                default:
                    error = $"  Unknown option: {token}";
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseTailOptions(
        string args,
        out AgentTailOptions options,
        out string? error)
    {
        options = new AgentTailOptions("", 20, false, TimeSpan.FromMilliseconds(500));
        error = null;

        var tokens = args
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count > 0 &&
            tokens[0].Equals("tail", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        if (tokens.Count == 0)
        {
            error = "  Missing background-run id.";
            return false;
        }

        var backgroundRunId = tokens[0];
        tokens.RemoveAt(0);

        var last = 20;
        var follow = false;
        var pollMs = 500;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token)
            {
                case "--follow":
                    follow = true;
                    break;

                case "--last":
                    if (i + 1 >= tokens.Count)
                    {
                        error = "  Missing value for --last.";
                        return false;
                    }

                    if (!int.TryParse(tokens[++i], out last) || last <= 0)
                    {
                        error = "  --last must be a positive integer.";
                        return false;
                    }

                    break;

                case "--poll-ms":
                    if (i + 1 >= tokens.Count)
                    {
                        error = "  Missing value for --poll-ms.";
                        return false;
                    }

                    if (!int.TryParse(tokens[++i], out pollMs) || pollMs <= 0)
                    {
                        error = "  --poll-ms must be a positive integer.";
                        return false;
                    }

                    break;

                default:
                    error = $"  Unknown option: {token}";
                    return false;
            }
        }

        options = new AgentTailOptions(
            backgroundRunId,
            last,
            follow,
            TimeSpan.FromMilliseconds(pollMs));
        return true;
    }

    private static bool TryParseWaitOptions(
        string args,
        out AgentWaitOptions options,
        out string? error)
    {
        options = new AgentWaitOptions([], AgentBackgroundRunWaitMode.All, TimeSpan.FromMilliseconds(500), null, false);
        error = null;

        var tokens = args
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count > 0 &&
            tokens[0].Equals("wait", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        if (tokens.Count == 0)
        {
            error = "  Missing background-run id.";
            return false;
        }

        var backgroundRunIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pollMs = 500;
        int? timeoutMs = null;
        var includeOutput = false;
        var waitMode = AgentBackgroundRunWaitMode.All;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token)
            {
                case "any" when backgroundRunIds.Count == 0:
                case "--any":
                    waitMode = AgentBackgroundRunWaitMode.Any;
                    break;

                case "all" when backgroundRunIds.Count == 0:
                case "--all":
                    waitMode = AgentBackgroundRunWaitMode.All;
                    break;

                case "--include-output":
                    includeOutput = true;
                    break;

                case "--poll-ms":
                    if (i + 1 >= tokens.Count)
                    {
                        error = "  Missing value for --poll-ms.";
                        return false;
                    }

                    if (!int.TryParse(tokens[++i], out pollMs) || pollMs <= 0)
                    {
                        error = "  --poll-ms must be a positive integer.";
                        return false;
                    }

                    break;

                case "--timeout-ms":
                    if (i + 1 >= tokens.Count)
                    {
                        error = "  Missing value for --timeout-ms.";
                        return false;
                    }

                    if (!int.TryParse(tokens[++i], out var parsedTimeout) || parsedTimeout <= 0)
                    {
                        error = "  --timeout-ms must be a positive integer.";
                        return false;
                    }

                    timeoutMs = parsedTimeout;
                    break;

                default:
                    if (token.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"  Unknown option: {token}";
                        return false;
                    }

                    if (seenIds.Add(token))
                        backgroundRunIds.Add(token);
                    break;
            }
        }

        if (backgroundRunIds.Count == 0)
        {
            error = "  Missing background-run id.";
            return false;
        }

        options = new AgentWaitOptions(
            backgroundRunIds,
            waitMode,
            TimeSpan.FromMilliseconds(pollMs),
            timeoutMs.HasValue ? TimeSpan.FromMilliseconds(timeoutMs.Value) : null,
            includeOutput);
        return true;
    }

    private static void WriteWaitCompletion(
        CommandContext context,
        AgentWaitOptions options,
        AgentBackgroundRunWaitBatchResult waitResult)
    {
        var elapsedMs = Math.Round(waitResult.Elapsed.TotalMilliseconds);
        if (options.BackgroundRunIds.Count == 1 &&
            waitResult.CompletedRuns.Count == 1)
        {
            var completed = waitResult.CompletedRuns[0];
            context.WriteLine(
                $"  {completed.BackgroundRunId} finished with status {completed.Run!.Status} after {elapsedMs}ms.");
        }
        else if (options.WaitMode == AgentBackgroundRunWaitMode.Any)
        {
            context.WriteLine(
                $"  Wait finished after {elapsedMs}ms. {waitResult.CompletedRuns.Count} background run(s) reached terminal states.");
        }
        else
        {
            context.WriteLine(
                $"  All {waitResult.CompletedRuns.Count} background run(s) finished after {elapsedMs}ms.");
        }

        if (waitResult.CompletedRuns.Count > 0)
        {
            context.WriteLine("  Completed runs:");
            foreach (var snapshot in waitResult.CompletedRuns)
                context.WriteLine($"    - {snapshot.BackgroundRunId}: {snapshot.Run!.Status}");
        }

        if (waitResult.PendingRuns.Count > 0)
        {
            context.WriteLine("  Still running:");
            foreach (var snapshot in waitResult.PendingRuns)
                context.WriteLine($"    - {snapshot.BackgroundRunId}: {snapshot.Run!.Status}");
        }

        if (!options.IncludeOutput)
            return;

        foreach (var snapshot in waitResult.CompletedRuns)
        {
            if (!AgentStatusFormatter.TryFormatDetails(
                    context.AgentTaskRuntime,
                    snapshot.BackgroundRunId,
                    includeOutput: true,
                    outputOffset: null,
                    outputLimit: null,
                    out var details))
            {
                continue;
            }

            context.WriteLine(details);
        }
    }

    private static string BuildWaitNotFoundMessage(AgentBackgroundRunWaitBatchResult waitResult) =>
        waitResult.MissingRunIds.Count == 1
            ? $"  No background run matched id '{waitResult.MissingRunIds[0]}'."
            : $"  No background runs matched these ids: {string.Join(", ", waitResult.MissingRunIds)}.";

    private static string BuildWaitTimedOutMessage(
        AgentWaitOptions options,
        AgentBackgroundRunWaitBatchResult waitResult)
    {
        var target = options.BackgroundRunIds.Count == 1
            ? options.BackgroundRunIds[0]
            : options.WaitMode == AgentBackgroundRunWaitMode.Any
                ? $"any of {options.BackgroundRunIds.Count} background runs"
                : $"all {options.BackgroundRunIds.Count} background runs";

        var statuses = waitResult.CompletedRuns
            .Concat(waitResult.PendingRuns)
            .Select(snapshot => $"{snapshot.BackgroundRunId}={snapshot.Run?.Status ?? AgentBackgroundRunStatus.Queued}")
            .ToArray();

        var suffix = statuses.Length > 0
            ? $" Current statuses: {string.Join(", ", statuses)}."
            : "";

        return $"  Timed out after {Math.Round(waitResult.Elapsed.TotalMilliseconds)}ms while waiting for {target}.{suffix}";
    }

    private static bool TryParsePruneOptions(
        string args,
        out AgentRetentionPolicy options,
        out string? error)
    {
        options = new AgentRetentionPolicy();
        error = null;

        var tokens = args
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count > 0 &&
            tokens[0] is "prune" or "archive")
        {
            tokens.RemoveAt(0);
        }

        var keepRuns = options.RetainTerminalBackgroundRuns;
        var keepWorkItems = options.RetainTerminalWorkItems;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (i + 1 >= tokens.Count)
            {
                error = $"  Missing value for {token}.";
                return false;
            }

            if (!int.TryParse(tokens[++i], out var value) || value < 0)
            {
                error = token switch
                {
                    "--keep-runs" => "  --keep-runs must be a non-negative integer.",
                    "--keep-work-items" or "--keep-items" => "  --keep-work-items must be a non-negative integer.",
                    _ => $"  Unknown option: {token}",
                };
                return false;
            }

            switch (token)
            {
                case "--keep-runs":
                    keepRuns = value;
                    break;

                case "--keep-work-items":
                case "--keep-items":
                    keepWorkItems = value;
                    break;

                default:
                    error = $"  Unknown option: {token}";
                    return false;
            }
        }

        options = new AgentRetentionPolicy
        {
            RetainTerminalBackgroundRuns = keepRuns,
            RetainTerminalWorkItems = keepWorkItems,
        };
        return true;
    }

    private static Task DelayAsync(CommandContext context, TimeSpan delay) =>
        context.DelayAsync?.Invoke(delay, context.CancellationToken) ??
        Task.Delay(delay, context.CancellationToken);

    private sealed record AgentTailOptions(
        string BackgroundRunId,
        int Last,
        bool Follow,
        TimeSpan PollInterval);

    private sealed record AgentWaitOptions(
        IReadOnlyList<string> BackgroundRunIds,
        AgentBackgroundRunWaitMode WaitMode,
        TimeSpan PollInterval,
        TimeSpan? Timeout,
        bool IncludeOutput);
}

/// <summary>
/// Represents model command.
/// </summary>
public class ModelCommand : ICommand
{
    public string Name => "model";
    public string Description => "Show or switch the current model";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            context.WriteLine($"  Current model: {context.QueryEngine.CurrentModel}");
            context.WriteLine($"  Common aliases: {string.Join(", ", ClaudeModels.CommonAliases)}");
        }
        else
        {
            var targetProvider = AiProviderSelection.DetectProvider(
                providerHint: null,
                model: args,
                fallbackProvider: context.AiProvider);

            if (targetProvider != context.AiProvider)
            {
                context.WriteLine(
                    $"  Switching providers requires a new session. Restart with --provider {AiProviderSelection.ToStorageValue(targetProvider)} --model {args.Trim()}.");
                return;
            }

            var resolved = await context.QueryEngine.SetModelAsync(
                AiProviderSelection.ResolveModel(args, context.AiProvider));
            context.WriteLine($"  Switched to: {resolved}");
        }
    }
}

/// <summary>
/// Represents effort command.
/// </summary>
public class EffortCommand : ICommand
{
    public string Name => "effort";
    public string Description => "Show or switch the current effort profile";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            context.WriteLine($"  Current effort: {context.QueryEngine.CurrentEffort}");
            context.WriteLine("  Available effort levels: Fast, Balanced, Thorough");
            return;
        }

        if (!Enum.TryParse<QueryEffortLevel>(args.Trim(), true, out var effort))
        {
            context.WriteLine($"  Unknown effort: {args.Trim()}");
            context.WriteLine("  Available effort levels: Fast, Balanced, Thorough");
            return;
        }

        await context.QueryEngine.SetEffortAsync(effort, context.CancellationToken);
        context.WriteLine($"  Switched effort to: {effort}");
    }
}

/// <summary>
/// Represents fast command.
/// </summary>
public class FastCommand : ICommand
{
    public string Name => "fast";
    public string Description => "Shortcut for /effort fast";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase))
        {
            await context.QueryEngine.SetEffortAsync(QueryEffortLevel.Fast, context.CancellationToken);
            context.WriteLine("  Switched effort to: Fast");
            return;
        }

        if (string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
        {
            await context.QueryEngine.SetEffortAsync(QueryEffortLevel.Balanced, context.CancellationToken);
            context.WriteLine("  Switched effort to: Balanced");
            return;
        }

        if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteLine($"  Current effort: {context.QueryEngine.CurrentEffort}");
            return;
        }

        context.WriteLine("  Usage: /fast [on|off|status]");
    }
}

/// <summary>
/// Represents session command.
/// </summary>
public class SessionCommand : ICommand
{
    public string Name => "session";
    public string Description => "Show current session metadata and transcript path";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var metadata = context.QueryEngine.SessionMetadata;
        var mode = metadata.Mode ?? context.PermissionContext.Mode;

        context.WriteLine($"  Session: {context.QueryEngine.SessionId ?? "(ephemeral)"}");
        if (!string.IsNullOrWhiteSpace(context.QueryEngine.TranscriptPath))
            context.WriteLine($"  Transcript: {context.QueryEngine.TranscriptPath}");
        context.WriteLine($"  Title: {metadata.Title ?? "(none)"}");
        context.WriteLine(
            metadata.Tags.Count == 0
                ? "  Tags: (none)"
                : $"  Tags: {string.Join(", ", metadata.Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))}");
        context.WriteLine($"  Mode: {mode}");
        context.WriteLine($"  Effort: {context.QueryEngine.CurrentEffort}");
        context.WriteLine($"  Auto-resume: {context.CurrentAgentAutoResumeMode.ToString().ToLowerInvariant()}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents mode command.
/// </summary>
public class ModeCommand : ICommand
{
    public string Name => "mode";
    public string Description => "Show or switch the current permission mode";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            var current = context.QueryEngine.SessionMetadata.Mode ?? context.PermissionContext.Mode;
            context.WriteLine($"  Current mode: {current}");
            context.WriteLine("  Available modes: Default, Plan, Auto, Bypass");
            return;
        }

        if (!Enum.TryParse<PermissionMode>(args.Trim(), true, out var mode))
        {
            context.WriteLine($"  Unknown mode: {args.Trim()}");
            context.WriteLine("  Available modes: Default, Plan, Auto, Bypass");
            return;
        }

        await context.QueryEngine.SetPermissionModeAsync(mode);
        context.WriteLine($"  Switched permission mode to: {mode}");
    }
}

/// <summary>
/// Represents plan command.
/// </summary>
public class PlanCommand : ICommand
{
    public string Name => "plan";
    public string Description => "Enter planning-only mode or exit it after approval";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();

        if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase))
        {
            if (context.QueryEngine.IsPlanModeActive)
            {
                context.WriteLine("  Plan mode: active");
                context.WriteLine($"  Resume mode after approval: {context.QueryEngine.PlanModeResumeMode}");
            }
            else
            {
                context.WriteLine("  Plan mode: inactive");
            }

            return;
        }

        if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "run", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "apply", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.QueryEngine.IsPlanModeActive)
            {
                context.WriteLine("  Plan mode is not active.");
                return;
            }

            var restoredMode = await context.QueryEngine.ExitPlanModeAsync(context.CancellationToken);
            context.WriteLine($"  Plan mode disabled. Restored permission mode: {restoredMode}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(trimmed) &&
            !string.Equals(trimmed, "enter", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteLine("  Usage: /plan [enter|status|exit]");
            return;
        }

        var changed = await context.QueryEngine.EnterPlanModeAsync(context.CancellationToken);
        var allowedTools = string.Join(", ", PlanModeToolPolicy.AllowedToolNamesInPlanMode);
        context.WriteLine(
            changed
                ? $"  Plan mode enabled. Available tools: {allowedTools}"
                : $"  Plan mode is already active. Available tools: {allowedTools}");
    }
}

/// <summary>
/// Represents title command.
/// </summary>
public class TitleCommand : ICommand
{
    public string Name => "title";
    public string Description => "Show, set, or clear the current session title";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            context.WriteLine($"  Current title: {context.QueryEngine.SessionMetadata.Title ?? "(none)"}");
            return;
        }

        if (string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase))
        {
            await context.QueryEngine.SetSessionTitleAsync(null);
            context.WriteLine("  Session title cleared.");
            return;
        }

        await context.QueryEngine.SetSessionTitleAsync(trimmed);
        context.WriteLine($"  Session title set to: {trimmed}");
    }
}

/// <summary>
/// Represents tag command.
/// </summary>
public class TagCommand : ICommand
{
    public string Name => "tag";
    public string Description => "Show or manage session tags";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            var tags = context.QueryEngine.SessionMetadata.Tags;
            context.WriteLine(
                tags.Count == 0
                    ? "  Tags: (none)"
                    : $"  Tags: {string.Join(", ", tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))}");
            context.WriteLine("  Usage: /tag add <name>, /tag remove <name>, /tag clear");
            return;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var action = parts[0];
        var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (action.ToLowerInvariant())
        {
            case "add":
                if (string.IsNullOrWhiteSpace(value))
                {
                    context.WriteLine("  Usage: /tag add <name>");
                    return;
                }

                await context.QueryEngine.AddSessionTagAsync(value);
                context.WriteLine($"  Added tag: {value}");
                break;

            case "remove":
            case "rm":
            case "delete":
                if (string.IsNullOrWhiteSpace(value))
                {
                    context.WriteLine("  Usage: /tag remove <name>");
                    return;
                }

                await context.QueryEngine.RemoveSessionTagAsync(value);
                context.WriteLine($"  Removed tag: {value}");
                break;

            case "clear":
                await context.QueryEngine.ClearSessionTagsAsync();
                context.WriteLine("  Cleared all session tags.");
                break;

            default:
                context.WriteLine("  Usage: /tag add <name>, /tag remove <name>, /tag clear");
                break;
        }
    }
}

/// <summary>
/// Represents compact command.
/// </summary>
public class CompactCommand : ICommand
{
    public string Name => "compact";
    public string Description => "Compact older conversation history into a resumable checkpoint";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var preserveTailCount = 8;
        if (!string.IsNullOrWhiteSpace(args) &&
            !int.TryParse(args.Trim(), out preserveTailCount))
        {
            context.WriteLine("  Usage: /compact [preserveTailCount]");
            return;
        }

        var result = await context.QueryEngine.CompactAsync(preserveTailCount);
        if (result == null)
        {
            context.WriteLine("  Not enough history to compact yet.");
            return;
        }

        context.WriteLine(
            $"  Compacted {result.RemovedMessageCount} messages and kept {result.ActiveMessages.Count - 1} recent messages in full.");
    }
}

/// <summary>
/// Represents session memory compact command.
/// </summary>
public class SessionMemoryCompactCommand : ICommand
{
    public string Name => "session-memory";
    public string Description => "Fold older history into a session-memory summary while keeping recent messages verbatim";
    public string[] Aliases => ["smcompact"];

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var preserveTailCount = 8;
        if (!string.IsNullOrWhiteSpace(args) &&
            !int.TryParse(args.Trim(), out preserveTailCount))
        {
            context.WriteLine("  Usage: /session-memory [preserveTailCount]");
            return;
        }

        var result = await context.QueryEngine.SessionMemoryCompactAsync(preserveTailCount);
        if (result == null)
        {
            context.WriteLine("  Not enough history to build a session-memory checkpoint yet.");
            return;
        }

        var boundaryNote = result.RewriteResult.Boundary.WasAdjusted
            ? $" Boundary adjusted from {result.RewriteResult.Boundary.RequestedIndex} to {result.RewriteResult.Boundary.AppliedIndex} to keep tool protocol intact."
            : string.Empty;

        context.WriteLine(
            $"  Folded {result.FoldedMessageCount} older messages into session memory and kept {result.ActiveMessages.Count - 1} recent messages verbatim.{boundaryNote}");
    }
}

/// <summary>
/// Represents partial compact command.
/// </summary>
public class PartialCompactCommand : ICommand
{
    public string Name => "pcompact";
    public string Description => "Compact a selected message range with from/up_to boundaries";
    public string[] Aliases => ["partial-compact"];

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var index))
        {
            context.WriteLine("  Usage: /pcompact <up_to|from> <index>");
            return;
        }

        ConversationCompactionResult? result = parts[0].ToLowerInvariant() switch
        {
            "up_to" or "upto" => await context.QueryEngine.CompactUpToAsync(index),
            "from" => await context.QueryEngine.CompactFromAsync(index),
            _ => null,
        };

        if (result == null)
        {
            if (parts[0].Equals("up_to", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Equals("upto", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                context.WriteLine("  No messages were compacted for that boundary.");
            }
            else
            {
                context.WriteLine("  Usage: /pcompact <up_to|from> <index>");
            }

            return;
        }

        var boundary = result.RewriteResult?.Boundary;
        var adjusted = boundary?.WasAdjusted == true
            ? $" Boundary adjusted from {boundary.RequestedIndex} to {boundary.AppliedIndex} to preserve tool_use/tool_result pairs."
            : string.Empty;

        context.WriteLine(
            $"  Compacted {result.RemovedMessageCount} messages with {parts[0]}={index}.{adjusted}");
    }
}

/// <summary>
/// Represents microcompact command.
/// </summary>
public class MicrocompactCommand : ICommand
{
    public string Name => "microcompact";
    public string Description => "Clear old tool results and thinking blocks without rewriting the whole conversation";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var preserveTailCount = 8;
        if (!string.IsNullOrWhiteSpace(args) &&
            !int.TryParse(args.Trim(), out preserveTailCount))
        {
            context.WriteLine("  Usage: /microcompact [preserveTailCount]");
            return;
        }

        var result = await context.QueryEngine.MicrocompactAsync(preserveTailCount);
        if (result == null)
        {
            context.WriteLine("  No old tool results or thinking blocks needed clearing.");
            return;
        }

        context.WriteLine(
            $"  Cleared {result.ClearedToolResultCount} tool-result messages and {result.ClearedThinkingBlockCount} thinking blocks.");
    }
}

/// <summary>
/// Represents away command.
/// </summary>
public class AwayCommand : ICommand
{
    public string Name => "away";
    public string Description => "Enter or exit away (AFK) mode";
    public string[] Aliases => ["afk"];

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();

        if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase))
        {
            if (context.QueryEngine.IsAwayModeActive)
            {
                var entered = context.QueryEngine.AwayEnteredAt;
                var reason = context.QueryEngine.AwayTriggerReason ?? "none";
                var elapsed = entered.HasValue
                    ? DateTimeOffset.UtcNow - entered.Value
                    : TimeSpan.Zero;
                context.WriteLine($"  Away mode: active (since {elapsed.TotalMinutes:F0}m ago, reason: {reason})");
            }
            else
            {
                context.WriteLine("  Away mode: inactive");
            }

            return;
        }

        if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "back", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "return", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.QueryEngine.IsAwayModeActive)
            {
                context.WriteLine("  Away mode is not active.");
                return;
            }

            var summary = await context.QueryEngine.ExitAwayModeAsync(context.CancellationToken);
            if (summary != null)
            {
                context.WriteLine($"  Welcome back! {summary.SummaryText}");
            }
            else
            {
                context.WriteLine("  Away mode exited.");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(trimmed) &&
            !string.Equals(trimmed, "enter", StringComparison.OrdinalIgnoreCase))
        {
            var reason = trimmed;
            var entered = await context.QueryEngine.EnterAwayModeAsync(reason, context.CancellationToken);
            context.WriteLine(
                entered
                    ? $"  Away mode enabled. Reason: {reason}"
                    : "  Away mode is already active.");
            return;
        }

        var defaultReason = "user initiated";
        var changed = await context.QueryEngine.EnterAwayModeAsync(defaultReason, context.CancellationToken);
        context.WriteLine(
            changed
                ? $"  Away mode enabled. Reason: {defaultReason}"
                : "  Away mode is already active.");
    }
}
