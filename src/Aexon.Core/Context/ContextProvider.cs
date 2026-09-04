using Aexon.Core.Markdown;
using Aexon.Core.Permissions;
using Aexon.Core.Query;
using Aexon.Core.Skills;
using Aexon.Core.Tools;

namespace Aexon.Core.Context;

/// <summary>
/// Provides context provider.
/// </summary>
public class ContextProvider
{
    private bool _managedMemoryContent;

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;
    public PermissionContext PermissionContext { get; set; } = new();
    public string? MemoryContent { get; set; }
    public string? SessionMemoryContent { get; set; }
    public string? UserClaudeDirectory { get; set; }
    public string? SystemClaudeDirectory { get; set; }
    public SkillLoader SkillLoader { get; set; } = new();
    public IReadOnlyList<MemoryInstructionDiagnostic> MemoryDiagnostics { get; private set; } = [];

    public PermissionContext GetPermissionContext()
    {
        PermissionContext.WorkingDirectory = WorkingDirectory;
        return PermissionContext;
    }

    /// <summary>
    /// Builds system prompt.
    /// </summary>
    public async Task<string> BuildSystemPromptAsync(
        IReadOnlyList<ITool> tools,
        QueryEngineConfig config)
    {
        if (_managedMemoryContent)
            await LoadMemoryAsync();

        var profile = QueryExecutionProfileResolver.Resolve(config);
        var parts = new List<string>();
        var planModeSection = GetPlanModeSection(tools);

        parts.Add(GetIdentityPrompt(profile));
        parts.Add(GetEnvironmentSection(profile));

        if (!string.IsNullOrWhiteSpace(planModeSection))
            parts.Add(planModeSection);

        var toolsSection = await BuildToolsSectionAsync(tools);
        if (!string.IsNullOrWhiteSpace(toolsSection))
            parts.Add(toolsSection);

        var skillsSection = BuildSkillsSection();
        if (!string.IsNullOrWhiteSpace(skillsSection))
            parts.Add(skillsSection);

        var contextSection = await BuildContextSectionAsync(profile);
        if (!string.IsNullOrWhiteSpace(contextSection))
            parts.Add(contextSection);

        var memorySection = BuildMemorySection();
        if (!string.IsNullOrWhiteSpace(memorySection))
            parts.Add(memorySection);

        if (!string.IsNullOrWhiteSpace(config.CustomSystemPrompt))
        {
            parts.Clear();
            parts.Add(config.CustomSystemPrompt);
            if (!string.IsNullOrWhiteSpace(planModeSection))
                parts.Add(planModeSection);
        }

        if (!string.IsNullOrWhiteSpace(config.AppendSystemPrompt))
            parts.Add(config.AppendSystemPrompt);

        return string.Join("\n\n", parts);
    }

    private async Task<string?> BuildToolsSectionAsync(IReadOnlyList<ITool> tools)
    {
        var toolPromptCtx = new ToolPromptContext
        {
            PermissionContext = GetPermissionContext(),
            Tools = tools,
        };

        var sections = new List<string>();
        foreach (var tool in tools)
        {
            try
            {
                var toolPrompt = await tool.GetPromptAsync(toolPromptCtx);
                if (!string.IsNullOrWhiteSpace(toolPrompt))
                    sections.Add($"## {tool.Name}\n{toolPrompt}");
            }
            catch
            {
                // Skip tools that fail to generate prompts.
            }
        }

        return sections.Count == 0
            ? null
            : "# Tools\n" + string.Join("\n\n", sections);
    }

    private string? BuildSkillsSection()
    {
        var skills = SkillLoader.Load(WorkingDirectory);
        if (skills.Count == 0)
            return null;

        var lines = skills.Values
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(skill => $"Available skills: {skill.Name} — {skill.Description}");

        return "# Skills\n" + string.Join("\n", lines);
    }

    private async Task<string?> BuildContextSectionAsync(QueryExecutionProfile profile)
    {
        var sections = new List<string>
        {
            $$"""
            ## Execution Profile
            - Effort: {{profile.Effort}}
            - Model: {{profile.ClaudeModel?.StableId ?? profile.ModelId}}
            - Prompt Detail: {{profile.PromptDetail}}
            - Max Output Tokens: {{profile.MaxOutputTokens}}
            - Extended Thinking: {{profile.ThinkingMode}}
            """
        };

        var gitStatus = await GetGitStatusAsync(includeRecentCommits: profile.PromptDetail != QueryPromptDetail.Compact);
        if (!string.IsNullOrWhiteSpace(gitStatus))
            sections.Add($"## Git Status\n{gitStatus}");

        return sections.Count == 0
            ? null
            : "# Context\n" + string.Join("\n\n", sections);
    }

    private string? BuildMemorySection()
    {
        var sections = new List<string>();

        if (MemoryDiagnostics.Count > 0)
        {
            sections.Add(
                "## CLAUDE.md Warnings\n" +
                string.Join(
                    "\n",
                    MemoryDiagnostics.Select(diagnostic =>
                        $"- {diagnostic.Path}: {diagnostic.Message}")));
        }

        if (!string.IsNullOrWhiteSpace(MemoryContent))
            sections.Add($"## CLAUDE.md Instructions\n{MemoryContent}");

        if (!string.IsNullOrWhiteSpace(SessionMemoryContent))
            sections.Add($"## Session Memory\n{SessionMemoryContent}");

        return sections.Count == 0
            ? null
            : "# Memory\n" + string.Join("\n\n", sections);
    }

    private string? GetPlanModeSection(IReadOnlyList<ITool> tools)
    {
        if (PermissionContext.Mode != PermissionMode.Plan)
            return null;

        var toolList = tools.Count == 0
            ? "(none)"
            : string.Join(", ", tools.Select(tool => tool.Name));

        return $$"""
            # Plan Mode
            You are currently in plan mode. Inspect the codebase and produce a structured implementation plan instead of making changes.

            Rules:
            - Do not modify files, git state, or external systems while this mode is active
            - Only use the tools available in this turn: {{toolList}}
            - Return the plan with these Markdown sections exactly: Goal, Findings, Implementation Steps, Risks, Verification
            - Do not call {{PlanModeToolPolicy.ExitPlanModeToolName}} until the user explicitly approves the plan and asks you to start executing
            """;
    }

    private string GetIdentityPrompt(QueryExecutionProfile profile)
    {
        return profile.PromptDetail switch
        {
            QueryPromptDetail.Compact => """
                # Identity
                You are Aexon, a CLI coding assistant working directly in the user's repository.

                Rules:
                - Use tools to inspect or change the code instead of describing steps
                - Keep responses short and task-focused
                - Prefer targeted verification before you finish
                - Ask only when a missing fact blocks safe execution
                """,
            QueryPromptDetail.Detailed => """
                # Identity
                You are Aexon, a rigorous CLI coding assistant working directly in the user's repository.

                Rules:
                - Inspect the existing code before editing and keep changes on the main execution path
                - Use tools for concrete work, not for narration
                - Preserve architecture boundaries and existing conventions
                - Surface correctness risks, migrations, or validation gaps when they affect the result
                - Verify the final behavior with targeted commands or tests before finishing
                """,
            _ => """
                # Identity
                You are Aexon, a CLI coding assistant working directly in the user's repository.

                Rules:
                - Prefer using tools over describing what should be done
                - Keep edits aligned with the existing codebase and architecture
                - Verify meaningful changes before finishing
                - Be concise unless extra detail is needed for correctness
                """,
        };
    }

    private string GetEnvironmentSection(QueryExecutionProfile profile)
    {
        var os = Environment.OSVersion.Platform switch
        {
            PlatformID.Unix => "macOS/Linux",
            PlatformID.Win32NT => "Windows",
            _ => "Unknown",
        };

        var shell = os == "Windows" ? "cmd/powershell" : "bash/zsh";
        var lines = new List<string>
        {
            "# Environment",
            $"- Operating System: {os}",
            $"- Working Directory: {WorkingDirectory}",
            $"- Current Date: {DateTime.Now:yyyy-MM-dd}",
            $"- Shell: {shell}",
        };

        if (profile.ClaudeFamily is { } family)
            lines.Add($"- Claude Family: {family}");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Collects the current Git branch, status, and recent commits.
    /// </summary>
    private async Task<string?> GetGitStatusAsync(bool includeRecentCommits)
    {
        try
        {
            var tasks = new List<Task<string?>>
            {
                RunGitCommandAsync("git rev-parse --abbrev-ref HEAD"),
                RunGitCommandAsync("git status --short"),
            };

            if (includeRecentCommits)
                tasks.Add(RunGitCommandAsync("git log --oneline -n 5"));

            var results = await Task.WhenAll(tasks);
            var branch = results[0]?.Trim() ?? "unknown";
            var status = results[1]?.Trim() ?? string.Empty;
            var log = includeRecentCommits && results.Length > 2
                ? results[2]?.Trim() ?? string.Empty
                : string.Empty;

            if (status.Length > 2000)
                status = status[..2000] + "\n... (truncated)";

            var sections = new List<string>
            {
                $"Current branch: {branch}",
                $"Status:\n{(string.IsNullOrEmpty(status) ? "(clean)" : status)}",
            };

            if (includeRecentCommits)
                sections.Add($"Recent commits:\n{log}");

            return string.Join("\n\n", sections);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> RunGitCommandAsync(string command)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd" : "bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command}\"",
                WorkingDirectory = WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads memory.
    /// </summary>
    public async Task LoadMemoryAsync(CancellationToken ct = default)
    {
        var result = await MemoryInstructionScanner.ScanAsync(
            new MemoryInstructionScanOptions
            {
                WorkingDirectory = WorkingDirectory,
                UserClaudeDirectory = UserClaudeDirectory,
                SystemClaudeDirectory = SystemClaudeDirectory,
            },
            ct);
        _managedMemoryContent = true;
        MemoryDiagnostics = result.Diagnostics;

        if (result.Files.Count == 0)
        {
            MemoryContent = null;
            return;
        }

        MemoryContent = string.Join(
            "\n\n",
            result.Files.Select(FormatMemoryFile));
    }

    private static string FormatMemoryFile(MemoryInstructionFile file)
    {
        var parts = new List<string> { $"# From {file.Scope}: {file.Path}" };

        if (file.Frontmatter.TryGetValue("paths", out var pathsValue))
        {
            var paths = FrontmatterParser.SplitPathValue(pathsValue);
            if (paths.Count > 0)
                parts.Add($"# Applies to: {string.Join(", ", paths)}");
        }

        parts.Add(file.Content);
        return string.Join("\n", parts);
    }
}
