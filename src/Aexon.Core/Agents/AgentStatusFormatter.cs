using System.Text;

namespace Aexon.Core.Agents;

/// <summary>
/// Defines list kinds supported by agent status overviews.
/// </summary>
public enum AgentStatusOverviewKind
{
    All,
    WorkItems,
    BackgroundRuns,
}

/// <summary>
/// Represents overview filtering and pagination options for agent status listings.
/// </summary>
public sealed record AgentStatusOverviewOptions
{
    public AgentStatusOverviewKind Kind { get; init; } = AgentStatusOverviewKind.All;
    public string? Status { get; init; }
    public string? Owner { get; init; }
    public int Offset { get; init; }
    public int? Limit { get; init; }
}

/// <summary>
/// Represents summary filtering options for agent status reports.
/// </summary>
public sealed record AgentStatusSummaryOptions
{
    public string? Owner { get; init; }
    public int RecentLimit { get; init; } = 3;
}

/// <summary>
/// Represents a page of background-run output entries.
/// </summary>
public sealed record AgentBackgroundRunOutputPage(
    int Offset,
    int TotalCount,
    IReadOnlyList<string> Entries)
{
    public int NextOffset => Offset + Entries.Count;
}

/// <summary>
/// Formats agent work item and background run state for tools and commands.
/// </summary>
public static class AgentStatusFormatter
{
    public static string FormatOverview(
        IAgentTaskRuntime runtime,
        AgentStatusOverviewOptions? options = null)
    {
        var resolvedOptions = options ?? new AgentStatusOverviewOptions();
        var workItems = runtime.ListWorkItems()
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(item => MatchesOwner(item.Owner, resolvedOptions.Owner))
            .Where(item => MatchesStatus(item.Status, resolvedOptions.Status))
            .ToArray();
        var backgroundRuns = runtime.ListBackgroundRuns()
            .OrderByDescending(run => run.UpdatedAt)
            .ThenBy(run => run.Id, StringComparer.OrdinalIgnoreCase)
            .Where(run => MatchesOwner(run.Owner, resolvedOptions.Owner))
            .Where(run => MatchesStatus(run.Status, resolvedOptions.Status))
            .ToArray();

        var builder = new StringBuilder();
        var includeWorkItems = resolvedOptions.Kind is AgentStatusOverviewKind.All or AgentStatusOverviewKind.WorkItems;
        var includeBackgroundRuns = resolvedOptions.Kind is AgentStatusOverviewKind.All or AgentStatusOverviewKind.BackgroundRuns;

        if (includeWorkItems)
        {
            AppendWorkItems(builder, workItems, resolvedOptions);
        }

        if (includeBackgroundRuns)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            AppendBackgroundRuns(builder, backgroundRuns, resolvedOptions);
        }

        if (builder.Length == 0)
        {
            return resolvedOptions.Kind switch
            {
                AgentStatusOverviewKind.WorkItems => "No work items matched the requested filters.",
                AgentStatusOverviewKind.BackgroundRuns => "No background runs matched the requested filters.",
                _ => "No agent work items or background runs matched the requested filters.",
            };
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatSummary(
        IAgentTaskRuntime runtime,
        AgentStatusSummaryOptions? options = null)
    {
        var resolvedOptions = options ?? new AgentStatusSummaryOptions();
        var recentLimit = Math.Max(1, resolvedOptions.RecentLimit);
        var workItems = runtime.ListWorkItems()
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(item => MatchesOwner(item.Owner, resolvedOptions.Owner))
            .ToArray();
        var backgroundRuns = runtime.ListBackgroundRuns()
            .OrderByDescending(run => run.UpdatedAt)
            .ThenBy(run => run.Id, StringComparer.OrdinalIgnoreCase)
            .Where(run => MatchesOwner(run.Owner, resolvedOptions.Owner))
            .ToArray();

        if (workItems.Length == 0 && backgroundRuns.Length == 0)
            return "No agent work items or background runs matched the requested filters.";

        var builder = new StringBuilder();
        builder.AppendLine(string.IsNullOrWhiteSpace(resolvedOptions.Owner)
            ? "Agent summary:"
            : $"Agent summary (owner: {resolvedOptions.Owner.Trim()}):");

        AppendStatusSummary(
            builder,
            "Work items",
            workItems.Select(item => item.Status));
        builder.AppendLine();
        AppendStatusSummary(
            builder,
            "Background runs",
            backgroundRuns.Select(run => run.Status));

        var activeRuns = backgroundRuns
            .Where(run => run.Status is AgentBackgroundRunStatus.Queued or
                AgentBackgroundRunStatus.Running or
                AgentBackgroundRunStatus.CancellationRequested)
            .Take(recentLimit)
            .ToArray();
        if (activeRuns.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Active background runs:");
            foreach (var run in activeRuns)
            {
                builder.AppendLine(
                    $"- {run.Id} [{run.Status}] {run.Name}{FormatWorkItemNote(run.WorkItemId)}");
            }
        }

        var finishedRuns = backgroundRuns
            .Where(run => run.Status is AgentBackgroundRunStatus.Stopped or
                AgentBackgroundRunStatus.Failed or
                AgentBackgroundRunStatus.Cancelled)
            .Take(recentLimit)
            .ToArray();
        if (finishedRuns.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Recent finished background runs:");
            foreach (var run in finishedRuns)
            {
                var reason = string.IsNullOrWhiteSpace(run.StopReason)
                    ? string.Empty
                    : $" ({run.StopReason})";
                builder.AppendLine(
                    $"- {run.Id} [{run.Status}] {run.Name}{FormatWorkItemNote(run.WorkItemId)}{reason}");
            }
        }

        var recentWorkItems = workItems
            .Take(recentLimit)
            .ToArray();
        if (recentWorkItems.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Recent work items:");
            foreach (var item in recentWorkItems)
                builder.AppendLine($"- {item.Id} [{item.Status}] {item.Title}{FormatWorkItemSourceSuffix(item)}{FormatWorkItemActionSuffix(item)}");
        }

        var attentionItems = AgentAttentionAnalyzer.ListAttentionItems(
                runtime,
                resolvedOptions.Owner,
                recentLimit)
            .ToArray();
        if (attentionItems.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Needs attention:");
            foreach (var item in attentionItems)
            {
                builder.AppendLine(
                    $"- {item.WorkItem.Id} [{item.WorkItem.Status}] {item.WorkItem.Title}: {item.NextAction}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatAttention(
        IAgentTaskRuntime runtime,
        string? owner = null,
        int? limit = null)
    {
        var items = AgentAttentionAnalyzer.ListAttentionItems(runtime, owner, limit);
        var builder = new StringBuilder();
        builder.AppendLine(string.IsNullOrWhiteSpace(owner)
            ? "Agent attention:"
            : $"Agent attention (owner: {owner.Trim()}):");
        builder.AppendLine($"Items: {items.Count}");

        if (items.Count == 0)
        {
            builder.AppendLine("Items: (none)");
            return builder.ToString().TrimEnd();
        }

        foreach (var item in items)
        {
            builder.AppendLine(
                $"- {item.WorkItem.Id} [{item.WorkItem.Status}] {item.WorkItem.Title}");
            builder.AppendLine($"  Summary: {item.Summary}");
            builder.AppendLine($"  Next: {item.NextAction}");
            if (!string.IsNullOrWhiteSpace(item.ActiveBackgroundRunId))
                builder.AppendLine($"  Active run: {item.ActiveBackgroundRunId}");
        }

        return builder.ToString().TrimEnd();
    }

    public static bool TryGetOutputPage(
        IAgentTaskRuntime runtime,
        string backgroundRunId,
        int offset,
        int? limit,
        out AgentBackgroundRun? run,
        out AgentBackgroundRunOutputPage page,
        out string? error)
    {
        run = runtime.GetBackgroundRun(backgroundRunId);
        if (run == null)
        {
            page = new AgentBackgroundRunOutputPage(0, 0, []);
            error = $"No background run matched id '{backgroundRunId}'.";
            return false;
        }

        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = limit.HasValue
            ? Math.Max(1, limit.Value)
            : int.MaxValue;
        var output = run.Output;
        var entries = output
            .Skip(normalizedOffset)
            .Take(normalizedLimit)
            .ToArray();

        page = new AgentBackgroundRunOutputPage(
            normalizedOffset,
            output.Count,
            entries);
        error = null;
        return true;
    }

    public static string FormatOutputPage(
        AgentBackgroundRun run,
        AgentBackgroundRunOutputPage page,
        bool includeRunHeader)
    {
        var builder = new StringBuilder();
        if (includeRunHeader)
        {
            builder.AppendLine($"Background run output: {run.Id}");
            builder.AppendLine($"Name: {run.Name}");
            builder.AppendLine($"Status: {run.Status}");
        }

        if (page.Entries.Count == 0)
        {
            builder.AppendLine(
                $"No output entries at or after offset {page.Offset}. Total output entries: {page.TotalCount}.");
            return builder.ToString().TrimEnd();
        }

        var start = page.Offset + 1;
        var end = page.Offset + page.Entries.Count;
        builder.AppendLine(
            page.Entries.Count == page.TotalCount && page.Offset == 0
                ? $"Showing all {page.TotalCount} output entr{(page.TotalCount == 1 ? "y" : "ies")}."
                : $"Showing output entries {start}-{end} of {page.TotalCount}.");

        foreach (var chunk in page.Entries)
        {
            builder.AppendLine("---");
            builder.AppendLine(chunk);
        }

        return builder.ToString().TrimEnd();
    }

    public static bool TryFormatDetails(
        IAgentTaskRuntime runtime,
        string id,
        bool includeOutput,
        int? outputOffset,
        int? outputLimit,
        out string details)
    {
        if (runtime.GetWorkItem(id) is { } workItem)
        {
            details = FormatWorkItem(runtime, workItem);
            return true;
        }

        if (runtime.GetBackgroundRun(id) is { } backgroundRun)
        {
            details = FormatBackgroundRun(
                backgroundRun,
                includeOutput,
                outputOffset,
                outputLimit);
            return true;
        }

        details = $"No agent work item or background run matched id '{id}'.";
        return false;
    }

    private static string FormatWorkItem(
        IAgentTaskRuntime runtime,
        AgentWorkItem item)
    {
        var builder = new StringBuilder();
        var activeOwnerRun = FindActiveOwnerRun(runtime, item.Owner);
        builder.AppendLine($"Work item: {item.Id}");
        builder.AppendLine($"Title: {item.Title}");
        if (!string.IsNullOrWhiteSpace(item.Description))
            builder.AppendLine($"Description: {item.Description}");
        if (!string.IsNullOrWhiteSpace(item.Owner))
            builder.AppendLine($"Owner: {item.Owner}");
        if (!string.IsNullOrWhiteSpace(item.SourceKind))
            builder.AppendLine($"Source: {FormatWorkItemSource(item)}");
        if (!string.IsNullOrWhiteSpace(item.ApprovalRequestId))
            builder.AppendLine($"Approval request: {item.ApprovalRequestId}");
        if (!string.IsNullOrWhiteSpace(item.ApprovalThreadId))
            builder.AppendLine($"Approval thread: {item.ApprovalThreadId}");
        if (AgentWorkItemApprovalCoordinator.DescribeApprovalState(item) is { } approvalState)
            builder.AppendLine($"Approval state: {approvalState}");
        if (AgentAttentionAnalyzer.DescribeNextAction(item, activeOwnerRun) is { } nextAction)
            builder.AppendLine($"Next action: {nextAction}");
        builder.AppendLine($"Status: {item.Status}");
        builder.AppendLine($"Created: {item.CreatedAt:O}");
        builder.AppendLine($"Updated: {item.UpdatedAt:O}");

        if (item.Blocks.Count > 0)
            builder.AppendLine($"Blocks: {string.Join(", ", item.Blocks)}");
        if (item.BlockedBy.Count > 0)
            builder.AppendLine($"Blocked by: {string.Join(", ", item.BlockedBy)}");

        return builder.ToString().TrimEnd();
    }

    private static string FormatBackgroundRun(
        AgentBackgroundRun run,
        bool includeOutput,
        int? outputOffset,
        int? outputLimit)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Background run: {run.Id}");
        builder.AppendLine($"Name: {run.Name}");
        if (!string.IsNullOrWhiteSpace(run.Owner))
            builder.AppendLine($"Owner: {run.Owner}");
        if (!string.IsNullOrWhiteSpace(run.WorkItemId))
            builder.AppendLine($"Work item: {run.WorkItemId}");
        builder.AppendLine($"Status: {run.Status}");
        builder.AppendLine(
            $"{(run.Status == AgentBackgroundRunStatus.Queued ? "Queued" : "Started")}: {run.StartedAt:O}");
        builder.AppendLine($"Updated: {run.UpdatedAt:O}");
        if (run.StoppedAt is not null)
            builder.AppendLine($"Stopped: {run.StoppedAt:O}");
        if (!string.IsNullOrWhiteSpace(run.StopReason))
            builder.AppendLine($"Stop reason: {run.StopReason}");

        if (includeOutput)
        {
            var output = run.Output;
            if (output.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Output:");
                builder.AppendLine(FormatOutputPage(
                    run,
                    new AgentBackgroundRunOutputPage(
                        Math.Max(0, outputOffset ?? 0),
                        output.Count,
                        output
                            .Skip(Math.Max(0, outputOffset ?? 0))
                            .Take(outputLimit.HasValue ? Math.Max(1, outputLimit.Value) : int.MaxValue)
                            .ToArray()),
                    includeRunHeader: false));
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static bool TryParseOverviewKind(
        string? value,
        out AgentStatusOverviewKind kind)
    {
        kind = AgentStatusOverviewKind.All;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = Normalize(value);
        kind = normalized switch
        {
            "all" => AgentStatusOverviewKind.All,
            "workitems" or "workitem" or "items" => AgentStatusOverviewKind.WorkItems,
            "backgroundruns" or "backgroundrun" or "runs" or "run" => AgentStatusOverviewKind.BackgroundRuns,
            _ => default,
        };

        return normalized is
            "all" or
            "workitems" or "workitem" or "items" or
            "backgroundruns" or "backgroundrun" or "runs" or "run";
    }

    public static bool TryParseView(
        string? value,
        out AgentStatusView view)
    {
        view = AgentStatusView.Overview;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = Normalize(value);
        view = normalized switch
        {
            "overview" or "list" => AgentStatusView.Overview,
            "summary" => AgentStatusView.Summary,
            "attention" => AgentStatusView.Attention,
            _ => default,
        };

        return normalized is "overview" or "list" or "summary" or "attention";
    }

    private static void AppendWorkItems(
        StringBuilder builder,
        IReadOnlyList<AgentWorkItem> items,
        AgentStatusOverviewOptions options)
    {
        if (items.Count == 0)
            return;

        var page = ApplyPagination(items, options.Offset, options.Limit);
        builder.AppendLine("Work items:");
        builder.AppendLine(FormatPaginationMessage("work item", items.Count, options.Offset, page.Count));
        foreach (var item in page)
            builder.AppendLine($"- {item.Id} [{item.Status}] {item.Title}{FormatWorkItemSourceSuffix(item)}{FormatWorkItemActionSuffix(item)}");
    }

    private static void AppendBackgroundRuns(
        StringBuilder builder,
        IReadOnlyList<AgentBackgroundRun> runs,
        AgentStatusOverviewOptions options)
    {
        if (runs.Count == 0)
            return;

        var page = ApplyPagination(runs, options.Offset, options.Limit);
        builder.AppendLine("Background runs:");
        builder.AppendLine(FormatPaginationMessage("background run", runs.Count, options.Offset, page.Count));
        foreach (var run in page)
        {
            var workItemNote = string.IsNullOrWhiteSpace(run.WorkItemId)
                ? string.Empty
                : $" -> {run.WorkItemId}";
            builder.AppendLine($"- {run.Id} [{run.Status}] {run.Name}{workItemNote}");
        }
    }

    private static void AppendStatusSummary<TStatus>(
        StringBuilder builder,
        string label,
        IEnumerable<TStatus> statuses)
        where TStatus : struct, Enum
    {
        var counts = statuses
            .GroupBy(status => status)
            .ToDictionary(group => group.Key, group => group.Count());
        var total = counts.Values.Sum();

        builder.AppendLine($"{label}: {total}");
        foreach (var status in Enum.GetValues<TStatus>())
        {
            if (counts.TryGetValue(status, out var count))
                builder.AppendLine($"- {status}: {count}");
        }
    }

    private static IReadOnlyList<T> ApplyPagination<T>(
        IReadOnlyList<T> source,
        int offset,
        int? limit)
    {
        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = limit.HasValue
            ? Math.Max(1, limit.Value)
            : int.MaxValue;

        return source
            .Skip(normalizedOffset)
            .Take(normalizedLimit)
            .ToArray();
    }

    private static string FormatPaginationMessage(
        string itemLabel,
        int totalCount,
        int offset,
        int pageCount)
    {
        var normalizedOffset = Math.Max(0, offset);
        if (pageCount == 0)
            return $"No {Pluralize(itemLabel, totalCount)} at or after offset {normalizedOffset}. Total matching {Pluralize(itemLabel, totalCount)}: {totalCount}.";

        var start = normalizedOffset + 1;
        var end = normalizedOffset + pageCount;
        return $"Showing {Pluralize(itemLabel, totalCount)} {start}-{end} of {totalCount}.";
    }

    private static string FormatWorkItemNote(string? workItemId) =>
        string.IsNullOrWhiteSpace(workItemId)
            ? string.Empty
            : $" -> {workItemId}";

    private static string FormatWorkItemSource(AgentWorkItem item)
    {
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.SourceId))
            ids.Add(item.SourceId!);
        if (!string.IsNullOrWhiteSpace(item.SourceThreadId))
            ids.Add(item.SourceThreadId!);

        return ids.Count == 0
            ? item.SourceKind!
            : $"{item.SourceKind} ({string.Join(", ", ids)})";
    }

    private static string FormatWorkItemSourceSuffix(AgentWorkItem item) =>
        string.IsNullOrWhiteSpace(item.SourceKind)
            ? string.Empty
            : $" [{item.SourceKind}]";

    private static string FormatWorkItemActionSuffix(AgentWorkItem item)
    {
        return AgentWorkItemApprovalCoordinator.DescribeApprovalState(item) is { } approvalState
            ? $" -> {approvalState}"
            : string.Empty;
    }

    private static AgentBackgroundRun? FindActiveOwnerRun(
        IAgentTaskRuntime runtime,
        string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return null;

        return runtime.ListBackgroundRuns()
            .Where(run => string.Equals(run.Owner, owner, StringComparison.OrdinalIgnoreCase))
            .Where(run => run.Status is AgentBackgroundRunStatus.Queued or
                AgentBackgroundRunStatus.Running or
                AgentBackgroundRunStatus.CancellationRequested)
            .OrderByDescending(run => run.UpdatedAt)
            .ThenBy(run => run.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool MatchesOwner(string? owner, string? filterOwner) =>
        string.IsNullOrWhiteSpace(filterOwner) ||
        string.Equals(owner, filterOwner.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesStatus<TStatus>(TStatus status, string? filterStatus)
        where TStatus : struct, Enum =>
        string.IsNullOrWhiteSpace(filterStatus) ||
        string.Equals(
            Normalize(status.ToString()),
            Normalize(filterStatus),
            StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string Pluralize(string label, int count) =>
        count == 1 ? label : $"{label}s";
}

/// <summary>
/// Defines high-level agent status views.
/// </summary>
public enum AgentStatusView
{
    Overview,
    Summary,
    Attention,
}
