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
        var estimate = UsageCostCalculator.Estimate(context.QueryEngine.CurrentModel, usage);

        context.WriteLine($"""

          Token Usage:
            Input:       {usage.InputTokens,10:N0}  (${estimate.InputCost:F4})
            Cache Write: {usage.CacheCreationInputTokens,10:N0}  (${estimate.CacheWriteCost:F4})
            Cache Read:  {usage.CacheReadInputTokens,10:N0}  (${estimate.CacheReadCost:F4})
            Output:      {usage.OutputTokens,10:N0}  (${estimate.OutputCost:F4})
            ───────────────────────────
            Input Total: {usage.TotalInputTokens,10:N0}
            Hit Rate:    {usage.CacheHitRate,10:P1}
            Total:       {usage.TotalTokens,10:N0}  (${estimate.TotalCost:F4})
            Messages:    {messages.Count,10:N0}
        """);
        return Task.CompletedTask;
    }
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

