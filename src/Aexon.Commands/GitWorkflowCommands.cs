using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aexon.Core.Agents;
using Aexon.Core.Auth;
using Aexon.Core.Commands;
using Aexon.Core.Compaction;
using Aexon.Core.Configuration;
using Aexon.Core.Context;
using Aexon.Core.Memory;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Query;
using Aexon.Core.Skills;
using Aexon.Core.Storage;
using Aexon.Core.Tools;

namespace Aexon.Commands;

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

