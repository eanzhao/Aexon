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
/// Represents version command.
/// </summary>
public sealed class VersionCommand(Assembly? productAssembly = null) : ICommand
{
    public string Name => "version";
    public string Description => "Show Aexon product and runtime version";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var assembly = productAssembly ?? Assembly.GetEntryAssembly() ?? typeof(VersionCommand).Assembly;
        context.WriteLine($"  Product version: {assembly.GetName().Version?.ToString() ?? "(unknown)"}");
        context.WriteLine($"  .NET runtime: {RuntimeInformation.FrameworkDescription} ({Environment.Version})");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents status command.
/// </summary>
public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Description => "Show the current session status snapshot";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var usage = context.QueryEngine.TotalUsage;
        var duration = context.SessionStartedAt.HasValue
            ? DateTimeOffset.UtcNow - context.SessionStartedAt.Value
            : TimeSpan.Zero;
        var activeSubagents = CountActiveSubagents(context.AgentTaskRuntime);

        context.WriteLine($"  Session ID: {context.QueryEngine.SessionId ?? "(ephemeral)"}");
        context.WriteLine($"  Model: {context.QueryEngine.CurrentModel}");
        context.WriteLine($"  Provider: {context.AiProvider}");
        context.WriteLine($"  Working directory: {context.PermissionContext.WorkingDirectory}");
        context.WriteLine($"  Session duration: {CommandFormatting.FormatDuration(duration)}");
        context.WriteLine($"  Total turns: {context.CurrentSessionTurnCount}");
        context.WriteLine($"  Tokens: input={usage.InputTokens:N0}, cache-write={usage.CacheCreationInputTokens:N0}, cache-read={usage.CacheReadInputTokens:N0}, output={usage.OutputTokens:N0}, total={usage.TotalTokens:N0}");
        context.WriteLine($"  Active subagents: {activeSubagents}");
        return Task.CompletedTask;
    }

    private static int CountActiveSubagents(IAgentTaskRuntime runtime)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in runtime.ListWorkItems())
        {
            if (item.Status is AgentWorkItemStatus.Completed or AgentWorkItemStatus.Cancelled)
                continue;

            if (!string.IsNullOrWhiteSpace(item.SubagentId))
                ids.Add(item.SubagentId);
        }

        foreach (var run in runtime.ListBackgroundRuns())
        {
            if (run.Status is AgentBackgroundRunStatus.Stopped or
                AgentBackgroundRunStatus.Failed or
                AgentBackgroundRunStatus.Cancelled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(run.SubagentId))
                ids.Add(run.SubagentId);
        }

        return ids.Count;
    }
}

/// <summary>
/// Represents resume command.
/// </summary>
public sealed class ResumeCommand(ITranscriptStore transcriptStore) : ICommand
{
    public string Name => "resume";
    public string Description => "List recent sessions and print resume guidance";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            await ListRecentSessionsAsync(context);
            return;
        }

        var session = await transcriptStore.FindSessionAsync(trimmed, context.CancellationToken);
        if (session == null)
        {
            context.WriteLine($"  No session matched '{trimmed}'.");
            return;
        }

        WriteStubGuidance(session, context);
    }

    private async Task ListRecentSessionsAsync(CommandContext context)
    {
        var sessions = (await transcriptStore.ListSessionsAsync(context.CancellationToken))
            .Take(10)
            .ToArray();
        if (sessions.Length == 0)
        {
            context.WriteLine("  No saved sessions were found.");
            return;
        }

        context.WriteLine("  Recent sessions:");
        for (var index = 0; index < sessions.Length; index++)
        {
            var session = sessions[index];
            context.WriteLine(
                $"    {index + 1}. {session.SessionId} | {session.UpdatedAt:yyyy-MM-dd HH:mm:ss zzz} | {session.Metadata.Title ?? "(untitled)"}");
        }

        if (context.ReadInputLine == null)
        {
            context.WriteLine("  In-process resume is not available in this context. Restart with `aexon --resume <id>` or `aexon --continue`.");
            return;
        }

        context.WriteLine("  Pick a session number to resume, or press Enter to cancel:");
        var raw = context.ReadInputLine()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            context.WriteLine("  Resume cancelled.");
            return;
        }

        if (!int.TryParse(raw, out var selectedIndex) ||
            selectedIndex < 1 ||
            selectedIndex > sessions.Length)
        {
            context.WriteLine($"  Invalid selection: {raw}");
            return;
        }

        WriteStubGuidance(sessions[selectedIndex - 1], context);
    }

    private static void WriteStubGuidance(TranscriptSession session, CommandContext context)
    {
        context.WriteLine($"  Selected session: {session.SessionId}");
        context.WriteLine("  In-process resume is not wired into the active REPL yet.");
        context.WriteLine($"  Restart with: aexon --resume {session.SessionId}");
        context.WriteLine("  Or restart the latest session with: aexon --continue");
    }
}

/// <summary>
/// Represents rename command.
/// </summary>
public sealed class RenameCommand : ICommand
{
    public string Name => "rename";
    public string Description => "Show or rename the current session title";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            context.WriteLine($"  Current title: {context.QueryEngine.SessionMetadata.Title ?? "(none)"}");
            return;
        }

        await context.QueryEngine.SetSessionTitleAsync(trimmed, context.CancellationToken);
        context.WriteLine($"  Session title renamed to: {trimmed}");
    }
}

/// <summary>
/// Represents stats command.
/// </summary>
public sealed class StatsCommand(ITranscriptStore transcriptStore) : ICommand
{
    private readonly ConversationRecovery _recovery = new();

    public string Name => "stats";
    public string Description => "Aggregate recent session usage and cost";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        if (!TryParseWindow(args, out var since, out var error))
        {
            context.WriteLine(error ?? "  Usage: /stats [--since <duration>]");
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - since;
        var sessions = (await transcriptStore.ListSessionsAsync(context.CancellationToken))
            .Where(session => session.UpdatedAt >= cutoff)
            .ToArray();

        var totalUsage = TokenUsage.Empty;
        double totalCost = 0;
        foreach (var session in sessions)
        {
            var projection = await transcriptStore.LoadProjectionAsync(
                session,
                new TranscriptLoadOptions(),
                context.CancellationToken);
            var usage = _recovery.Recover(projection).TotalUsage;
            totalUsage += usage;
            totalCost += UsageCostCalculator.Estimate(session.Model, usage).TotalCost;
        }

        context.WriteLine($"  Window: last {CommandFormatting.FormatWindow(since)}");
        context.WriteLine($"  Total sessions: {sessions.Length}");
        context.WriteLine($"  Total tokens: {totalUsage.TotalTokens:N0}");
        context.WriteLine($"  Input tokens: {totalUsage.InputTokens:N0}");
        context.WriteLine($"  Cache write tokens: {totalUsage.CacheCreationInputTokens:N0}");
        context.WriteLine($"  Cache read tokens: {totalUsage.CacheReadInputTokens:N0}");
        context.WriteLine($"  Output tokens: {totalUsage.OutputTokens:N0}");
        context.WriteLine($"  Estimated cost: ${totalCost:F4}");
    }

    private static bool TryParseWindow(
        string args,
        out TimeSpan since,
        out string? error)
    {
        since = TimeSpan.FromDays(30);
        error = null;

        if (string.IsNullOrWhiteSpace(args))
            return true;

        var parts = args.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            string.Equals(parts[0], "--since", StringComparison.OrdinalIgnoreCase) &&
            CommandFormatting.TryParseDuration(parts[1], out since))
        {
            return true;
        }

        error = "  Usage: /stats [--since <duration>]";
        return false;
    }
}

internal static class UsageCostCalculator
{
    public static UsageCostEstimate Estimate(string modelOrAlias, TokenUsage usage)
    {
        var pricing = ResolvePricing(modelOrAlias);
        var inputCost = usage.InputTokens * pricing.InputPerMillion / 1_000_000;
        var outputCost = usage.OutputTokens * pricing.OutputPerMillion / 1_000_000;
        var cacheReadCost = usage.CacheReadInputTokens * pricing.CacheReadPerMillion / 1_000_000;
        var cacheWriteCost = usage.CacheCreationInputTokens * pricing.CacheWritePerMillion / 1_000_000;

        return new UsageCostEstimate(
            inputCost,
            cacheWriteCost,
            cacheReadCost,
            outputCost,
            inputCost + cacheWriteCost + cacheReadCost + outputCost);
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

    internal sealed record UsageCostEstimate(
        double InputCost,
        double CacheWriteCost,
        double CacheReadCost,
        double OutputCost,
        double TotalCost);

    private sealed record UsagePricing(
        double InputPerMillion,
        double CacheWritePerMillion,
        double CacheReadPerMillion,
        double OutputPerMillion);
}

internal static class CommandFormatting
{
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";

        return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    }

    public static string FormatWindow(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{duration.TotalDays:0.#} day(s)";
        if (duration.TotalHours >= 1)
            return $"{duration.TotalHours:0.#} hour(s)";
        if (duration.TotalMinutes >= 1)
            return $"{duration.TotalMinutes:0.#} minute(s)";

        return $"{duration.TotalSeconds:0.#} second(s)";
    }

    public static bool TryParseDuration(string raw, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out duration))
            return duration > TimeSpan.Zero;

        if (trimmed.Length < 2)
            return false;

        var suffix = char.ToLowerInvariant(trimmed[^1]);
        if (!double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            return false;
        }

        duration = suffix switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            'w' => TimeSpan.FromDays(value * 7),
            _ => TimeSpan.Zero,
        };

        return duration > TimeSpan.Zero;
    }
}
