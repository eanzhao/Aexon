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
        _runtime ?? context.AgentTeamRuntime
        ?? throw new InvalidOperationException(
            "The /team command requires an IAgentTeamRuntime, but none was supplied by the command or its CommandContext.");

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
        _runtime ?? context.AgentMessageRuntime
        ?? throw new InvalidOperationException(
            "The /mailbox command requires an IAgentMessageRuntime, but none was supplied by the command or its CommandContext.");
}

