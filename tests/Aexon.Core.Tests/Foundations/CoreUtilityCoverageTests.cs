using System.Text.Json;
using Aexon.Core.Agents;
using Aexon.Core.Configuration;
using Aexon.Core.Hooks;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Providers;
using Aexon.Core.Query;
using Aexon.Core.Tests.Runtime;
using Aexon.Core.Todos;
using Aexon.Core.Tools;
using Aexon.Tools;

namespace Aexon.Core.Tests.Foundations;

/// <summary>
/// Contains focused tests for smaller core utility types.
/// </summary>
public sealed class CoreUtilityCoverageTests
{
    [Fact]
    public void SettingsFileLocator_ReturnsExplicitPathOrDefaultCandidates()
    {
        var explicitOnly = SettingsFileLocator.GetCandidatePaths("/work", "/tmp/custom.json");
        var defaults = SettingsFileLocator.GetCandidatePaths("/work");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(["/tmp/custom.json"], explicitOnly);
        Assert.Equal(
            [
                Path.Combine(home, ".aexon", "settings.json"),
                Path.Combine(home, ".claude", "settings.json"),
                Path.Combine("/work", ".aexon", "settings.json"),
                Path.Combine("/work", ".claude", "settings.json"),
            ],
            defaults);
    }

    [Fact]
    public void AgentSettingsAndLoadResult_BuildRetentionPolicyAndSummary()
    {
        var settings = new AgentSettings
        {
            BackgroundRunConcurrency = 3,
            RetainCompletedBackgroundRuns = 5,
            RetainCompletedWorkItems = 7,
            AutoResumeMode = AgentAutoResumeMode.Latest,
        };

        var policy = settings.BuildRetentionPolicy();
        var summary = new AgentSettingsLoadResult(
            settings,
            ["Loaded diagnostics"],
            ["/tmp/settings.json"]).StartupSummary;
        var emptySummary = new AgentSettingsLoadResult(new AgentSettings(), [], []).StartupSummary;

        Assert.Equal(5, policy.RetainTerminalBackgroundRuns);
        Assert.Equal(7, policy.RetainTerminalWorkItems);
        Assert.Contains("Loaded diagnostics", summary, StringComparison.Ordinal);
        Assert.Contains("background concurrency set to 3", summary, StringComparison.Ordinal);
        Assert.Contains("retain 5 completed background runs and 7 completed work items", summary, StringComparison.Ordinal);
        Assert.Contains("auto-resume mode set to latest", summary, StringComparison.Ordinal);
        Assert.Null(emptySummary);
    }

    [Fact]
    public void AgentSettingsLoader_Load_UsesExplicitPathAndReportsNonObjectAgents()
    {
        using var temp = new TempDirectory();
        var configPath = temp.WriteFile("custom/settings.json", """
{
  "agents": []
}
""");

        var result = AgentSettingsLoader.Load(temp.Root, configPath);

        Assert.Equal(Path.GetFullPath(configPath), Assert.Single(result.SourcePaths));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("non-object agents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ToolDefaults_ProvideSharedInterfaceBehavior()
    {
        ITool tool = new MinimalTool();
        var context = new ToolExecutionContext
        {
            WorkingDirectory = "/tmp",
            PermissionContext = new PermissionContext(),
            Tools = [],
            Messages = [],
            CancellationToken = CancellationToken.None,
        };

        var validation = await tool.ValidateInputAsync(JsonSerializer.SerializeToElement(new { }), context);
        var permission = await tool.CheckPermissionsAsync(JsonSerializer.SerializeToElement(new { }), context);

        Assert.True(validation.IsValid);
        Assert.Equal(PermissionBehavior.Allow, permission.Behavior);
        Assert.True(tool.IsEnabled());
        Assert.False(tool.IsReadOnly(default));
        Assert.False(tool.IsDestructive(default));
        Assert.False(tool.IsConcurrencySafe(default));
        Assert.Equal("minimal", tool.GetUserFacingName());
        Assert.Null(tool.GetActivityDescription(null));
        Assert.Equal(100_000, tool.MaxResultSizeChars);
    }

    [Fact]
    public void HookRuntimeBuilder_Build_ReportsLoadedCommands()
    {
        using var temp = new TempDirectory();
        var configPath = temp.WriteFile("settings.json", """
{
  "hooks": {
    "pre_tool_use": [
      "echo test"
    ]
  }
}
""");

        var result = HookRuntimeBuilder.Build(temp.Root, configPath);

        Assert.Equal(1, result.CommandCount);
        Assert.Equal(Path.GetFullPath(configPath), Assert.Single(result.SourcePaths));
        Assert.Contains(
            result.StartupMessages,
            message => message.Contains("loaded 1 command(s) across 1 event(s)", StringComparison.Ordinal));
        Assert.Contains("loaded 1 command(s)", result.StartupSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultToolSchemas_StayWithinAnthropicOptionalParameterBudget()
    {
        var runtime = new InMemoryAgentTaskRuntime();
        var tools = new ITool[]
        {
            new BashTool(),
            new FileReadTool(),
            new FileWriteTool(),
            new FileEditTool(),
            new NotebookEditTool(),
            new GlobTool(),
            new GrepTool(),
            new AskUserQuestionTool(),
            new TodoWriteTool(new InMemoryTodoRuntime()),
            new WebFetchTool(),
            new WebSearchTool(new DefaultProviderCapabilityRouter(), () => ClaudeModels.DefaultMainModel),
            new AgentTool(new NoOpAgentRunner(), new DefaultProviderCapabilityRouter(), runtime, hooks: HookRuntime.Empty),
            new AgentStatusTool(runtime),
            new AgentResumeTool(runtime, new InMemoryAgentMessageRuntime(), new InMemoryAgentMessageActivationRuntime()),
            new AgentStopTool(runtime),
            new AgentWaitTool(runtime),
            new SendMessageTool(new InMemoryAgentMessageRuntime()),
            new MailboxStatusTool(new InMemoryAgentMessageRuntime()),
            new MailboxRespondTool(new InMemoryAgentMessageRuntime()),
        };

        var totalOptionalParameters = tools
            .Where(tool => tool.IsEnabled())
            .Sum(CountOptionalParameters);

        Assert.True(
            totalOptionalParameters <= 24,
            $"Expected total optional schema parameters to stay at or below Anthropic's limit of 24, but found {totalOptionalParameters}.");
    }

    private static int CountOptionalParameters(ITool tool)
    {
        var schema = tool.GetInputSchema();
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var required = schema.TryGetProperty("required", out var requiredArray) &&
            requiredArray.ValueKind == JsonValueKind.Array
            ? requiredArray.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        return properties.EnumerateObject()
            .Count(property => !required.Contains(property.Name));
    }

    private sealed class MinimalTool : ITool
    {
        public string Name => "minimal";

        public Task<string> GetDescriptionAsync() => Task.FromResult("minimal tool");

        public JsonElement GetInputSchema() =>
            JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<string> GetPromptAsync(ToolPromptContext context) =>
            Task.FromResult("minimal prompt");

        public Task<ToolResult> ExecuteAsync(
            JsonElement input,
            ToolExecutionContext context,
            IProgress<ToolProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class NoOpAgentRunner : IAgentExecutionRunner
    {
        public Task<AgentExecutionResult> RunAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentExecutionResult(
                Summary: "ok",
                Success: true,
                Usage: TokenUsage.Empty,
                TurnCount: 1));
    }
}
