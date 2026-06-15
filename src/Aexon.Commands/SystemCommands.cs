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
/// Represents a dynamically loaded skill slash command.
/// </summary>
public sealed class SkillCommand : ICommand
{
    private readonly Skill _skill;

    public SkillCommand(Skill skill)
    {
        _skill = skill;
    }

    public string Name => _skill.Name;

    public string Description => _skill.Description;

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        if (context.SubmitPromptAsync == null)
        {
            context.WriteLine("  Skill commands are unavailable in this context.");
            return;
        }

        await context.SubmitPromptAsync(BuildPrompt(args));
    }

    private string BuildPrompt(string args)
    {
        var trimmedArgs = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmedArgs))
        {
            return $"""
                The user invoked /{_skill.Name}.
                First call SkillTool with name="{_skill.Name}".
                After the tool returns, follow that skill for the current task and continue normally.
                """;
        }

        return $"""
            The user invoked /{_skill.Name} with this request:
            {trimmedArgs}

            First call SkillTool with name="{_skill.Name}".
            After the tool returns, use that skill to handle the request above.
            """;
    }
}

/// <summary>
/// Describes NyxID runtime config surfaced to slash commands.
/// </summary>
public sealed record NyxIdRuntimeConfig(
    string DefaultBaseUrl,
    string ActiveBaseUrl,
    bool HasStoredCredentials,
    bool BaseUrlFromEnvironment);

/// <summary>
/// Represents config command.
/// </summary>
public sealed class ConfigCommand(
    NyxIdCredentialStore credentialStore,
    ManagedSettingsLoadResult managedSettings,
    NyxIdRuntimeConfig nyxIdRuntimeConfig) : ICommand
{
    public string Name => "config";
    public string Description => "Show runtime config and config source details";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "list", StringComparison.OrdinalIgnoreCase))
        {
            WriteEntries(context, BuildEntries(context));
            return Task.CompletedTask;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            string.Equals(parts[0], "get", StringComparison.OrdinalIgnoreCase))
        {
            var entries = BuildEntries(context);
            if (entries.TryGetValue(parts[1], out var value))
            {
                context.WriteLine($"  {parts[1]}: {value}");
                return Task.CompletedTask;
            }

            context.WriteLine($"  Unknown config key: {parts[1]}");
            context.WriteLine($"  Available keys: {string.Join(", ", entries.Keys)}");
            return Task.CompletedTask;
        }

        context.WriteLine("  Usage: /config [list], /config get <key>");
        return Task.CompletedTask;
    }

    private Dictionary<string, string> BuildEntries(CommandContext context)
    {
        var nyxIdSource = nyxIdRuntimeConfig.BaseUrlFromEnvironment
            ? "env:NYXID_BASE_URL"
            : nyxIdRuntimeConfig.HasStoredCredentials
                ? "stored-credentials/default"
                : "default";
        var credentials = credentialStore.Load();

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = context.QueryEngine.CurrentModel,
            ["provider"] = context.AiProvider.ToString(),
            ["effort"] = context.QueryEngine.CurrentEffort.ToString(),
            ["workingDirectory"] = context.PermissionContext.WorkingDirectory,
            ["permissionMode"] = context.PermissionContext.Mode.ToString(),
            ["managed.settingsSources"] = managedSettings.SourcePaths.Count == 0
                ? "(none)"
                : string.Join(", ", managedSettings.SourcePaths),
            ["managed.activeSource"] = managedSettings.Settings.SourcePath ?? "(none)",
            ["nyxid.defaultBaseUrl"] = nyxIdRuntimeConfig.DefaultBaseUrl,
            ["nyxid.activeBaseUrl"] = nyxIdRuntimeConfig.ActiveBaseUrl,
            ["nyxid.baseUrlSource"] = nyxIdSource,
            ["nyxid.hasStoredCredentials"] = nyxIdRuntimeConfig.HasStoredCredentials ? "true" : "false",
            ["llm.defaultProvider"] = credentials?.DefaultProvider ?? "(not set)",
            ["llm.defaultModel"] = credentials?.DefaultModel ?? "(not set)",
        };
    }

    private static void WriteEntries(CommandContext context, IReadOnlyDictionary<string, string> entries)
    {
        context.WriteLine("  Runtime config:");
        foreach (var entry in entries)
            context.WriteLine($"    {entry.Key}: {entry.Value}");
    }
}

/// <summary>
/// Represents permissions command.
/// </summary>
public sealed class PermissionsCommand : ICommand
{
    public string Name => "permissions";
    public string Description => "Show or clear current permission rules";

    public Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "list", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "show", StringComparison.OrdinalIgnoreCase))
        {
            WritePermissions(context);
            return Task.CompletedTask;
        }

        if (string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase))
            return ClearPermissionsAsync(context);

        context.WriteLine("  Usage: /permissions [list|show], /permissions clear");
        return Task.CompletedTask;
    }

    private static void WritePermissions(CommandContext context)
    {
        context.WriteLine($"  Mode: {context.PermissionContext.Mode}");
        if (context.PermissionContext.Rules.Count == 0)
        {
            context.WriteLine("  Rules: (none)");
            return;
        }

        for (var index = 0; index < context.PermissionContext.Rules.Count; index++)
        {
            var rule = context.PermissionContext.Rules[index];
            context.WriteLine(
                $"  Rule {index + 1}: ToolName={rule.ToolName}, RuleContent={rule.RuleContent ?? "(none)"}, Behavior={rule.Behavior}");
        }
    }

    private static Task ClearPermissionsAsync(CommandContext context)
    {
        var totalRules =
            context.PermissionContext.Rules.Count +
            context.PermissionContext.ToolRules.Count +
            context.PermissionContext.AlwaysAllowRules.Count +
            context.PermissionContext.AlwaysAskRules.Count +
            context.PermissionContext.AlwaysDenyRules.Count;

        if (totalRules == 0)
        {
            context.WriteLine("  No permission rules are currently set.");
            return Task.CompletedTask;
        }

        if (context.ReadInputLine == null)
        {
            context.WriteLine("  Confirmation input is unavailable in this context.");
            return Task.CompletedTask;
        }

        context.WriteLine($"  Type 'yes' to clear {totalRules} permission rule(s):");
        var confirmation = context.ReadInputLine()?.Trim();
        if (!string.Equals(confirmation, "yes", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteLine("  Permission rule clear cancelled.");
            return Task.CompletedTask;
        }

        context.PermissionContext.Rules.Clear();
        context.PermissionContext.ToolRules.Clear();
        context.PermissionContext.AlwaysAllowRules.Clear();
        context.PermissionContext.AlwaysAskRules.Clear();
        context.PermissionContext.AlwaysDenyRules.Clear();
        context.WriteLine($"  Cleared {totalRules} permission rule(s).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents memory command.
/// </summary>
public sealed class MemoryCommand(
    MemdirLayout memdirLayout,
    string? userClaudeDirectory = null,
    string? systemClaudeDirectory = null) : ICommand
{
    public string Name => "memory";
    public string Description => "List, show, or search loaded memory files";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "list", StringComparison.OrdinalIgnoreCase))
        {
            await ListAsync(context);
            return;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && string.Equals(parts[0], "show", StringComparison.OrdinalIgnoreCase))
        {
            await ShowAsync(parts[1], context);
            return;
        }

        if (parts.Length == 2 && string.Equals(parts[0], "search", StringComparison.OrdinalIgnoreCase))
        {
            await SearchAsync(parts[1], context);
            return;
        }

        context.WriteLine("  Usage: /memory [list], /memory show <name>, /memory search <term>");
    }

    private async Task ListAsync(CommandContext context)
    {
        var files = await GetKnownFilesAsync(context);
        if (files.Count == 0)
        {
            context.WriteLine("  No memory files were found.");
            return;
        }

        context.WriteLine("  Memory files:");
        foreach (var file in files)
        {
            var modified = file.ModifiedAt.HasValue
                ? file.ModifiedAt.Value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                : "(unknown)";
            context.WriteLine($"    {file.Name}: {file.Path} (modified {modified})");
        }
    }

    private async Task ShowAsync(string name, CommandContext context)
    {
        var file = await ResolveFileAsync(name, context);
        if (file == null)
        {
            context.WriteLine($"  No memory file matched '{name}'.");
            return;
        }

        var lines = await File.ReadAllLinesAsync(file.Path, context.CancellationToken);
        context.WriteLine($"  {file.Name}: {file.Path}");
        foreach (var line in lines.Take(200))
            context.WriteLine(line);

        if (lines.Length > 200)
            context.WriteLine($"  ... truncated after 200 lines ({lines.Length - 200} more line(s))");
    }

    private async Task SearchAsync(string term, CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            context.WriteLine("  Search term is required.");
            return;
        }

        var files = await GetKnownFilesAsync(context);
        var matches = new List<string>();
        foreach (var file in files)
        {
            var lines = await File.ReadAllLinesAsync(file.Path, context.CancellationToken);
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].Contains(term, StringComparison.OrdinalIgnoreCase))
                    matches.Add($"{file.Path}:{index + 1}: {lines[index].Trim()}");
            }
        }

        if (matches.Count == 0)
        {
            context.WriteLine($"  No memory matches found for '{term}'.");
            return;
        }

        context.WriteLine($"  Matches for '{term}':");
        foreach (var match in matches)
            context.WriteLine($"    {match}");
    }

    private async Task<KnownMemoryFile?> ResolveFileAsync(string name, CommandContext context)
    {
        var files = await GetKnownFilesAsync(context);
        return files.FirstOrDefault(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<KnownMemoryFile>> GetKnownFilesAsync(CommandContext context)
    {
        var files = new List<KnownMemoryFile>();
        var scan = await MemoryInstructionScanner.ScanAsync(
            new MemoryInstructionScanOptions
            {
                WorkingDirectory = context.PermissionContext.WorkingDirectory,
                UserClaudeDirectory = userClaudeDirectory,
                SystemClaudeDirectory = systemClaudeDirectory,
            },
            context.CancellationToken);

        foreach (var group in scan.Files.GroupBy(file => file.Scope))
        {
            var grouped = group.ToArray();
            var prefix = group.Key.ToString().ToLowerInvariant();
            for (var index = 0; index < grouped.Length; index++)
            {
                var item = grouped[index];
                files.Add(new KnownMemoryFile(
                    grouped.Length == 1 ? prefix : $"{prefix}-{index + 1}",
                    item.Path,
                    File.Exists(item.Path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(item.Path), TimeSpan.Zero) : null));
            }
        }

        AddMemdirFile(files, "memdir-project", memdirLayout.MemoryIndexPath);

        if (Directory.Exists(memdirLayout.SessionMemoryDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(memdirLayout.SessionMemoryDirectory, "SESSION_MEMORY.md", SearchOption.AllDirectories))
            {
                var directoryName = Path.GetFileName(Path.GetDirectoryName(path)) ?? "session";
                AddMemdirFile(files, $"memdir-session-{directoryName}", path);
            }
        }

        if (Directory.Exists(memdirLayout.TeamMemoryDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(memdirLayout.TeamMemoryDirectory, "TEAM_MEMORY.md", SearchOption.AllDirectories))
            {
                var directoryName = Path.GetFileName(Path.GetDirectoryName(path)) ?? "team";
                AddMemdirFile(files, $"memdir-team-{directoryName}", path);
            }
        }

        return files
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddMemdirFile(
        ICollection<KnownMemoryFile> files,
        string name,
        string path)
    {
        if (!File.Exists(path))
            return;

        files.Add(new KnownMemoryFile(name, path, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)));
    }

    private sealed record KnownMemoryFile(
        string Name,
        string Path,
        DateTimeOffset? ModifiedAt);
}

/// <summary>
/// Represents init command.
/// </summary>
public sealed class InitCommand : ICommand
{
    public string Name => "init";
    public string Description => "Scaffold a CLAUDE.md file in the working directory";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var force = string.Equals(args.Trim(), "--force", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(args) && !force)
        {
            context.WriteLine("  Usage: /init [--force]");
            return;
        }

        var path = Path.Combine(context.PermissionContext.WorkingDirectory, "CLAUDE.md");
        if (File.Exists(path) && !force)
        {
            context.WriteLine($"  Refusing to overwrite existing file: {path}");
            context.WriteLine("  Re-run with /init --force to overwrite it.");
            return;
        }

        await File.WriteAllTextAsync(path, ClaudeInitTemplate, context.CancellationToken);
        context.WriteLine($"  Wrote CLAUDE.md scaffold: {path}");
    }

    private const string ClaudeInitTemplate =
        """
        # Project Description
        - TODO: describe what this project does and who it serves

        ## Tech Stack
        - Runtime:
        - Frameworks:
        - Tooling:

        ## Conventions
        - Architecture:
        - Coding style:
        - Review expectations:

        ## Test Commands
        - Build:
        - Test:
        - Format/Lint:
        """;
}

/// <summary>
/// Represents doctor command.
/// </summary>
public class DoctorCommand(
    NyxIdCredentialStore credentialStore,
    NyxIdRuntimeConfig nyxIdRuntimeConfig) : ICommand
{
    public string Name => "doctor";
    public string Description => "Run local diagnostics for the current Aexon session";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var checks = new List<(bool Success, string Message)>
        {
            await CheckDotnetAsync(context),
            await CheckGitBinaryAsync(context),
            await CheckGitRepositoryAsync(context),
            CheckNyxIdLogin(),
            await CheckNyxIdAsync(context),
        };

        var passCount = checks.Count(check => check.Success);
        foreach (var check in checks)
            context.WriteLine($"{(check.Success ? "[OK]" : "[FAIL]")} {check.Message}");

        context.WriteLine($"  Summary: {passCount} passed, {checks.Count - passCount} failed.");
    }

    protected virtual async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    protected virtual async Task<HttpStatusCode> GetStatusCodeAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        return response.StatusCode;
    }

    private async Task<(bool Success, string Message)> CheckDotnetAsync(CommandContext context)
    {
        try
        {
            var result = await RunProcessAsync("dotnet", "--version", context.PermissionContext.WorkingDirectory, context.CancellationToken);
            return result.ExitCode == 0
                ? (true, $"dotnet SDK: {result.StdOut.Trim()}")
                : (false, $"dotnet SDK: {NormalizeError(result)}");
        }
        catch (Exception ex)
        {
            return (false, $"dotnet SDK: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Message)> CheckGitBinaryAsync(CommandContext context)
    {
        try
        {
            var result = await RunProcessAsync("git", "--version", context.PermissionContext.WorkingDirectory, context.CancellationToken);
            return result.ExitCode == 0
                ? (true, $"git binary: {result.StdOut.Trim()}")
                : (false, $"git binary: {NormalizeError(result)}");
        }
        catch (Exception ex)
        {
            return (false, $"git binary: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Message)> CheckGitRepositoryAsync(CommandContext context)
    {
        try
        {
            var result = await RunProcessAsync(
                "git",
                "rev-parse --is-inside-work-tree",
                context.PermissionContext.WorkingDirectory,
                context.CancellationToken);
            return result.ExitCode == 0 && result.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                ? (true, $"working directory is a git repo: {context.PermissionContext.WorkingDirectory}")
                : (false, $"working directory is a git repo: {NormalizeError(result)}");
        }
        catch (Exception ex)
        {
            return (false, $"working directory is a git repo: {ex.Message}");
        }
    }

    private (bool Success, string Message) CheckNyxIdLogin()
    {
        var credentials = credentialStore.Load();
        if (credentials == null)
            return (false, "NyxID login: not signed in. Run `aexon login`.");

        if (string.IsNullOrWhiteSpace(credentials.DefaultProvider))
            return (false, "NyxID login: signed in, but no default LLM provider. Run `aexon llm`.");

        var modelSuffix = string.IsNullOrWhiteSpace(credentials.DefaultModel)
            ? string.Empty
            : $" ({credentials.DefaultModel})";
        return (true, $"NyxID login: {credentials.DefaultProvider}{modelSuffix}");
    }

    private async Task<(bool Success, string Message)> CheckNyxIdAsync(CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(nyxIdRuntimeConfig.ActiveBaseUrl) ||
            !Uri.TryCreate(nyxIdRuntimeConfig.ActiveBaseUrl, UriKind.Absolute, out var uri))
        {
            return (false, "NyxID connectivity: base URL is not configured.");
        }

        try
        {
            var statusCode = await GetStatusCodeAsync(uri, new Dictionary<string, string>(), context.CancellationToken);
            return (true, $"NyxID connectivity: HTTP {(int)statusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"NyxID connectivity: {ex.Message}");
        }
    }

    private static string NormalizeError((int ExitCode, string StdOut, string StdErr) result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr)
            ? result.StdOut
            : result.StdErr;
        text = text.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? $"exit code {result.ExitCode}"
            : text;
    }
}

