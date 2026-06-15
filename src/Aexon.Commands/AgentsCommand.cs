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

