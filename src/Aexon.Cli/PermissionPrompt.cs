using System.Text.Json;
using Spectre.Console;

namespace Aexon.Cli;

/// <summary>
/// Represents the risk classification for a permission request.
/// </summary>
internal enum PermissionRiskLevel
{
    Safe,
    Caution,
    Dangerous,
}

/// <summary>
/// Represents the supported user decisions for a permission request.
/// </summary>
internal enum PermissionPromptDecision
{
    Yes,
    No,
    AlwaysAllow,
}

/// <summary>
/// Describes a structured permission prompt for rendering and testing.
/// </summary>
internal sealed record PermissionPromptViewModel(
    string ToolName,
    string ParameterSummary,
    PermissionRiskLevel RiskLevel,
    string RiskLabel,
    string RiskColor,
    string Description,
    string? ProminentLabel,
    string? ProminentValue);

/// <summary>
/// Renders permission prompts in a structured Spectre.Console layout.
/// </summary>
internal sealed class PermissionPrompt
{
    private static readonly string[] SafeCommandPrefixes =
    [
        "pwd",
        "ls",
        "rg",
        "grep",
        "find",
        "cat",
        "head",
        "tail",
        "wc",
        "which",
        "git status",
        "git diff",
        "git show",
        "git log",
        "git rev-parse"
    ];

    private static readonly string[] DangerousCommandTokens =
    [
        "rm ",
        " rm",
        "del ",
        "rmdir",
        "chmod",
        "chown",
        "git reset",
        "git clean",
        "git checkout",
        "git switch",
        "git restore",
        "git commit",
        "git push",
        "git rebase",
        "git merge",
        "git branch -d",
        "git branch -D",
        "docker rm",
        "docker system prune",
        "kubectl delete",
        "dropdb",
        "truncate ",
        "mkfs",
        "shutdown",
        "reboot",
        ">",
        ">>",
        "| sh",
        "| bash"
    ];

    public PermissionPromptDecision Prompt(string toolName, string description, JsonElement input)
    {
        var viewModel = BuildViewModel(toolName, description, input);

        if (Console.IsOutputRedirected)
            return PermissionPromptDecision.No;

        Render(viewModel);
        return ReadDecision();
    }

    internal static PermissionPromptViewModel BuildViewModel(
        string toolName,
        string description,
        JsonElement input)
    {
        var riskLevel = AssessRisk(toolName, input);
        var prominent = ExtractProminentDetail(toolName, input);

        return new PermissionPromptViewModel(
            ToolName: toolName,
            ParameterSummary: ToolProgressRenderer.SummarizeParameters(input),
            RiskLevel: riskLevel,
            RiskLabel: GetRiskLabel(riskLevel),
            RiskColor: GetRiskColor(riskLevel),
            Description: description,
            ProminentLabel: prominent?.Label,
            ProminentValue: prominent?.Value);
    }

    internal static string GetRiskColor(PermissionRiskLevel riskLevel) =>
        riskLevel switch
        {
            PermissionRiskLevel.Safe => "green",
            PermissionRiskLevel.Caution => "yellow",
            PermissionRiskLevel.Dangerous => "red",
            _ => "yellow",
        };

    internal static string GetRiskLabel(PermissionRiskLevel riskLevel) =>
        riskLevel switch
        {
            PermissionRiskLevel.Safe => "Safe",
            PermissionRiskLevel.Caution => "Caution",
            PermissionRiskLevel.Dangerous => "Dangerous",
            _ => "Caution",
        };

    internal static string? ExtractRuleTarget(JsonElement input)
    {
        if (TryGetStringProperty(input, "command", out var command))
            return command;

        if (TryGetStringProperty(input, "file_path", out var filePath))
            return filePath;

        if (TryGetStringProperty(input, "path", out var path))
            return path;

        return null;
    }

    private static PermissionRiskLevel AssessRisk(string toolName, JsonElement input)
    {
        if (TryGetStringProperty(input, "command", out var command))
            return AssessCommandRisk(command);

        var normalizedTool = toolName.Trim();
        if (MatchesAny(normalizedTool, "Read", "Glob", "Grep", "WebFetch", "WebSearch", "AskUserQuestion", "AgentWait", "AgentStatus", "MailboxStatus"))
            return PermissionRiskLevel.Safe;

        if (MatchesAny(normalizedTool, "Delete", "Stop", "Dissolve"))
            return PermissionRiskLevel.Dangerous;

        if (MatchesAny(normalizedTool, "Write", "Edit", "Create", "Respond"))
            return PermissionRiskLevel.Caution;

        return PermissionRiskLevel.Caution;
    }

    private static PermissionRiskLevel AssessCommandRisk(string command)
    {
        var normalized = command.Trim();
        if (string.IsNullOrEmpty(normalized))
            return PermissionRiskLevel.Caution;

        if (DangerousCommandTokens.Any(token =>
                normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return PermissionRiskLevel.Dangerous;
        }

        if (SafeCommandPrefixes.Any(prefix =>
                normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return PermissionRiskLevel.Safe;
        }

        return PermissionRiskLevel.Caution;
    }

    private static bool MatchesAny(string value, params string[] candidates) =>
        candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static (string Label, string Value)? ExtractProminentDetail(
        string toolName,
        JsonElement input)
    {
        if (TryGetStringProperty(input, "command", out var command))
            return ("Command", command);

        if (TryGetStringProperty(input, "file_path", out var filePath))
            return ("File", filePath);

        if (toolName.Contains("Read", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("Edit", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetStringProperty(input, "path", out var path))
                return ("File", path);
        }

        return null;
    }

    private static bool TryGetStringProperty(
        JsonElement input,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void Render(PermissionPromptViewModel viewModel)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn();
        grid.AddRow("[grey]Tool[/]", $"[bold]{Markup.Escape(viewModel.ToolName)}[/]");

        if (!string.IsNullOrWhiteSpace(viewModel.ProminentLabel) &&
            !string.IsNullOrWhiteSpace(viewModel.ProminentValue))
        {
            grid.AddRow(
                $"[grey]{Markup.Escape(viewModel.ProminentLabel)}[/]",
                $"[bold]{Markup.Escape(viewModel.ProminentValue)}[/]");
        }

        grid.AddRow("[grey]Parameters[/]", Markup.Escape(viewModel.ParameterSummary));
        grid.AddRow(
            "[grey]Risk[/]",
            $"[{viewModel.RiskColor}]{Markup.Escape(viewModel.RiskLabel)}[/]");

        if (!string.IsNullOrWhiteSpace(viewModel.Description))
            grid.AddRow("[grey]Reason[/]", Markup.Escape(viewModel.Description));

        grid.AddRow(
            "[grey]Options[/]",
            "[green][[Y]][/]es / [red][[N]][/]o / [yellow][[A]][/]lways allow");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(grid)
        {
            Header = new PanelHeader("Permission Required"),
            Border = BoxBorder.Rounded,
        });
        AnsiConsole.Markup("Choice [[Y/N/A]]: ");
    }

    private static PermissionPromptDecision ReadDecision()
    {
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            AnsiConsole.WriteLine();

            switch (key.Key)
            {
                case ConsoleKey.Y:
                    return PermissionPromptDecision.Yes;
                case ConsoleKey.N:
                case ConsoleKey.Enter:
                case ConsoleKey.Escape:
                    return PermissionPromptDecision.No;
                case ConsoleKey.A:
                    return PermissionPromptDecision.AlwaysAllow;
            }

            AnsiConsole.Markup("Choice [[Y/N/A]]: ");
        }
    }
}
