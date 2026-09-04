using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;
using Aexon.Tools.Shell;

namespace Aexon.Tools;

/// <summary>
/// Represents the input payload for the Bash tool.
/// </summary>
public class BashToolInput
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("timeout")]
    public int? TimeoutMs { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Executes shell commands in the user's working directory.
/// </summary>
public class BashTool : ITool
{
    private const int DefaultTimeoutMs = 120_000;  // 2 minutes
    private const int MaxTimeoutMs = 600_000;      // 10 minutes

    public string Name => "Bash";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Execute a bash command in the user's shell.");

    public JsonElement GetInputSchema()
    {
        var schema = """
        {
            "type": "object",
            "properties": {
                "command": {
                    "type": "string",
                    "description": "The bash command to execute"
                },
                "timeout": {
                    "type": "integer",
                    "description": "Optional timeout in milliseconds (default: 120000, max: 600000)"
                },
                "description": {
                    "type": "string",
                    "description": "Brief description of what this command does (for the user)"
                }
            },
            "required": ["command"],
            "additionalProperties": false
        }
        """;
        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            Executes a given bash command and returns its output.

            Important guidelines:
            - Use this tool to run shell commands
            - Prefer dedicated tools (Read, Write, Edit, Grep) over bash equivalents
            - Always quote file paths with spaces
            - Use absolute paths when possible
            - When running multiple independent commands, make separate tool calls
            - For sequential commands, chain with &&
            - Do not use interactive commands (git rebase -i, vim, etc.)
            - Default timeout is 2 minutes, maximum is 10 minutes
            """);
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<BashToolInput>(input);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Command))
            return ToolResult.Error("Command is required.");

        var timeout = TimeSpan.FromMilliseconds(
            Math.Min(parsed.TimeoutMs ?? DefaultTimeoutMs, MaxTimeoutMs));

        var shell = GetShell();
        var shellArgs = GetShellArgs(parsed.Command);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = shellArgs,
            WorkingDirectory = context.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["PAGER"] = "cat" },
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
                progress?.Report(new ToolProgress("", "bash_output", e.Data));
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            await process.WaitForExitAsync(cts.Token);
            var interpretation = CommandSemantics.Interpret(
                parsed.Command,
                process.ExitCode,
                stdout.ToString(),
                stderr.ToString());

            var formatted = FormatOutput(
                stdout.ToString(),
                stderr.ToString(),
                process.ExitCode,
                interpretation);

            return CreateCommandResult(
                parsed.Command,
                context.WorkingDirectory,
                process.ExitCode,
                interpretation.IsError,
                formatted);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }

            return CreateCommandResult(
                parsed.Command,
                context.WorkingDirectory,
                exitCode: null,
                isError: true,
                $"Command timed out after {timeout.TotalSeconds}s.\n" +
                $"Partial output:\n{stdout}");
        }
        catch (Exception ex)
        {
            return CreateCommandResult(
                parsed.Command,
                context.WorkingDirectory,
                exitCode: null,
                isError: true,
                $"Failed to execute command: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks whether the requested command needs approval.
    /// </summary>
    public Task<PermissionResult> CheckPermissionsAsync(
        JsonElement input, ToolExecutionContext context)
    {
        var command = input.TryGetProperty("command", out var cmdProp)
            ? cmdProp.GetString() ?? ""
            : "";

        var classification = BashCommandClassifier.Classify(command);
        var risk = DangerousCommandDetector.Classify(command);

        if (classification.Category == BashCommandCategory.ReadOnly &&
            risk.RiskLevel == DangerousCommandRiskLevel.Safe)
        {
            return Task.FromResult(PermissionResult.Allow());
        }

        if (risk.RiskLevel == DangerousCommandRiskLevel.Dangerous)
        {
            var reason = string.IsNullOrWhiteSpace(risk.Reason)
                ? "Dangerous command detected"
                : risk.Reason;
            return Task.FromResult(
                PermissionResult.Ask($"Dangerous command requires explicit approval: {command} ({reason})"));
        }

        var message = classification.Category switch
        {
            BashCommandCategory.Destructive => $"Potentially destructive command: {command}",
            BashCommandCategory.Write => $"Allow command that may modify files or git state: {command}",
            _ => $"Allow running: {command}",
        };

        if (risk.RiskLevel == DangerousCommandRiskLevel.Caution)
        {
            var reason = string.IsNullOrWhiteSpace(risk.Reason)
                ? "Caution"
                : risk.Reason;
            message = $"{message} [{reason}]";
        }

        return Task.FromResult(PermissionResult.Ask(message));
    }

    public bool IsReadOnly(JsonElement input)
    {
        var command = input.TryGetProperty("command", out var cmdProp)
            ? cmdProp.GetString() ?? ""
            : "";
        return BashCommandClassifier.Classify(command).Category == BashCommandCategory.ReadOnly;
    }

    public bool IsConcurrencySafe(JsonElement input) => IsReadOnly(input);

    public string GetUserFacingName(JsonElement? input)
    {
        if (input?.TryGetProperty("command", out var cmd) == true)
        {
            var command = cmd.GetString() ?? "";
            return command.Length > 60 ? $"Bash: {command[..57]}..." : $"Bash: {command}";
        }
        return "Bash";
    }

    public string? GetActivityDescription(JsonElement? input)
    {
        if (input?.TryGetProperty("command", out var cmd) == true)
            return $"Running {cmd.GetString()}";
        return "Running command";
    }

    public int MaxResultSizeChars => 200_000;

    // ─── Private helpers ──────────────────────────────

    private static string GetShell()
    {
        if (OperatingSystem.IsWindows())
            return "cmd.exe";

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        return shell;
    }

    private static string GetShellArgs(string command)
    {
        if (OperatingSystem.IsWindows())
            return $"/c {command}";
        return $"-c \"{command.Replace("\"", "\\\"")}\"";
    }

    private static string FormatOutput(
        string stdout,
        string stderr,
        int exitCode,
        CommandInterpretation interpretation)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(stdout))
            sb.Append(stdout.TrimEnd());

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"STDERR:\n{stderr.TrimEnd()}");
        }

        if (!string.IsNullOrWhiteSpace(interpretation.Message))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"Note: {interpretation.Message}");
        }

        if (exitCode != 0 && interpretation.IsError)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"Exit code: {exitCode}");
        }

        return sb.Length > 0 ? sb.ToString() : "(no output)";
    }

    private static ToolResult CreateCommandResult(
        string command,
        string workingDirectory,
        int? exitCode,
        bool isError,
        string data)
    {
        var message = new SystemLocalCommandMessage
        {
            Content = $"Local command executed: {command}",
            Command = command,
            WorkingDirectory = workingDirectory,
            ExitCode = exitCode,
            IsError = isError,
        };

        return new ToolResult
        {
            Data = data,
            IsError = isError,
            NewMessages = [message],
        };
    }
}
