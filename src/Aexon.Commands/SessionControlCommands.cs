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

