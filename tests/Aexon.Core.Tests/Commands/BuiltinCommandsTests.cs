using System.Reflection;
using Aexon.Commands;
using Aexon.Core.Agents;
using Aexon.Core.Commands;
using Aexon.Core.Context;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Query;
using Aexon.Core.Storage;
using Aexon.Core.Tests.Runtime;
using Aexon.Core.Tools;

namespace Aexon.Core.Tests.Commands;

/// <summary>
/// Contains tests for builtin slash commands other than agents.
/// </summary>
public sealed class BuiltinCommandsTests
{
    [Fact]
    public async Task HelpAndExitCommands_RenderAvailableCommandsAndRequestExit()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        var lines = new List<string>();
        var exitRequested = false;
        var commands = new ICommand[] { new HelpCommand(), new ExitCommand() };
        var context = CreateContext(bundle.Engine, bundle.PermissionContext, lines, commands);
        var exitContext = new CommandContext
        {
            WriteLine = context.WriteLine,
            Tools = context.Tools,
            QueryEngine = context.QueryEngine,
            PermissionContext = context.PermissionContext,
            AgentTaskRuntime = context.AgentTaskRuntime,
            Commands = context.Commands,
            DelayAsync = context.DelayAsync,
            CancellationToken = context.CancellationToken,
            RequestExit = () => exitRequested = true,
            RequestClear = context.RequestClear,
        };

        await new HelpCommand().ExecuteAsync("", exitContext);
        await new ExitCommand().ExecuteAsync("", exitContext);

        var output = string.Join(Environment.NewLine, lines);
        Assert.Contains("Available commands", output, StringComparison.Ordinal);
        Assert.Contains("/help", output, StringComparison.Ordinal);
        Assert.Contains("/exit", output, StringComparison.Ordinal);
        Assert.Contains("/quit", output, StringComparison.Ordinal);
        Assert.True(exitRequested);
    }

    [Fact]
    public void GitWorkflowCommands_AreRegisteredInCliCommandRegistry()
    {
        var registry = BuildCliCommandRegistry();

        Assert.IsType<DiffCommand>(registry.Get("diff"));
        Assert.IsType<ReviewCommand>(registry.Get("review"));
        Assert.IsType<CommitCommand>(registry.Get("commit"));
        Assert.IsType<BranchCommand>(registry.Get("branch"));
        Assert.IsType<PrCommand>(registry.Get("pr"));
    }

    [Fact]
    public async Task GitWorkflowPromptCommands_InjectExpectedPrompts()
    {
        var diffPrompt = await ExecutePromptCommandAsync(new DiffCommand());
        Assert.Contains("git diff --cached", diffPrompt, StringComparison.Ordinal);
        Assert.Contains("group the summary by file or logical area", diffPrompt, StringComparison.OrdinalIgnoreCase);

        var reviewPrompt = await ExecutePromptCommandAsync(new ReviewCommand());
        Assert.Contains("origin/dev", reviewPrompt, StringComparison.Ordinal);
        Assert.Contains("then dev, then main", reviewPrompt, StringComparison.Ordinal);
        Assert.Contains("SQL safety", reviewPrompt, StringComparison.Ordinal);
        Assert.Contains("file:line", reviewPrompt, StringComparison.Ordinal);

        var commitPrompt = await ExecutePromptCommandAsync(new CommitCommand());
        Assert.Contains("git status", commitPrompt, StringComparison.Ordinal);
        Assert.Contains("git diff", commitPrompt, StringComparison.Ordinal);
        Assert.Contains("git log --oneline -20", commitPrompt, StringComparison.Ordinal);
        Assert.Contains("--no-verify", commitPrompt, StringComparison.Ordinal);
        Assert.Contains("--amend", commitPrompt, StringComparison.Ordinal);

        var prPrompt = await ExecutePromptCommandAsync(new PrCommand());
        Assert.Contains("gh pr create", prPrompt, StringComparison.Ordinal);
        Assert.Contains("## Summary", prPrompt, StringComparison.Ordinal);
        Assert.Contains("## Test plan", prPrompt, StringComparison.Ordinal);
        Assert.Contains("origin/dev", prPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BranchCommand_RejectsUnsafeBranchNames()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        var lines = new List<string>();

        await new BranchCommand().ExecuteAsync(
            "feature;rm",
            CreateContext(bundle.Engine, bundle.PermissionContext, lines));

        var output = string.Join(Environment.NewLine, lines);
        Assert.Contains("Usage: /branch [name]", output, StringComparison.Ordinal);
        Assert.Contains("may only contain letters, numbers", output, StringComparison.Ordinal);
        Assert.Empty(bundle.Engine.Messages);
    }

    [Fact]
    public async Task ClearAndCostCommands_ReportUsageAndResetConversation()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(
            temp.Root,
            initialMessages:
            [
                UserMessage.FromText("hello"),
                new AssistantMessage { Content = [new TextBlock("world")] },
            ],
            initialUsage: new TokenUsage
            {
                InputTokens = 2_000,
                OutputTokens = 1_000,
                CacheReadInputTokens = 500,
                CacheCreationInputTokens = 250,
            });
        var lines = new List<string>();
        var clearRequested = false;
        var context = CreateContext(bundle.Engine, bundle.PermissionContext, lines);
        var clearContext = new CommandContext
        {
            WriteLine = context.WriteLine,
            Tools = context.Tools,
            QueryEngine = context.QueryEngine,
            PermissionContext = context.PermissionContext,
            AgentTaskRuntime = context.AgentTaskRuntime,
            Commands = context.Commands,
            DelayAsync = context.DelayAsync,
            CancellationToken = context.CancellationToken,
            RequestExit = context.RequestExit,
            RequestClear = () => clearRequested = true,
        };

        await new CostCommand().ExecuteAsync("", clearContext);
        await new ClearCommand().ExecuteAsync("", clearContext);

        var output = string.Join(Environment.NewLine, lines);
        Assert.Contains("Token Usage:", output, StringComparison.Ordinal);
        Assert.Contains("Cache Write:", output, StringComparison.Ordinal);
        Assert.Contains("Hit Rate:", output, StringComparison.Ordinal);
        Assert.Contains("Messages:", output, StringComparison.Ordinal);
        Assert.Contains("Conversation cleared.", output, StringComparison.Ordinal);
        Assert.Empty(bundle.Engine.Messages);
        Assert.True(clearRequested);
        Assert.Equal(1, bundle.Journal.ResetHeadCount);
    }

    [Fact]
    public async Task MetadataCommands_ShowAndMutateSessionState()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        var lines = new List<string>();
        var context = CreateContext(bundle.Engine, bundle.PermissionContext, lines);

        await new ModelCommand().ExecuteAsync("", context);
        await new ModelCommand().ExecuteAsync("opus", context);
        await new ModelCommand().ExecuteAsync("gpt-4o", context);
        await new EffortCommand().ExecuteAsync("", context);
        await new EffortCommand().ExecuteAsync("thorough", context);
        await new FastCommand().ExecuteAsync("", context);
        await new FastCommand().ExecuteAsync("off", context);
        await new EffortCommand().ExecuteAsync("mystery", context);
        await new ModeCommand().ExecuteAsync("", context);
        await new ModeCommand().ExecuteAsync("plan", context);
        await new ModeCommand().ExecuteAsync("mystery", context);
        await new TitleCommand().ExecuteAsync("", context);
        await new TitleCommand().ExecuteAsync("Sprint 7", context);
        await new TagCommand().ExecuteAsync("", context);
        await new TagCommand().ExecuteAsync("add Alpha", context);
        await new TagCommand().ExecuteAsync("add beta", context);
        await new TagCommand().ExecuteAsync("remove alpha", context);
        await new TagCommand().ExecuteAsync("unknown", context);
        await new SessionCommand().ExecuteAsync("", context);
        await new TitleCommand().ExecuteAsync("clear", context);
        await new TagCommand().ExecuteAsync("clear", context);

        var output = string.Join(Environment.NewLine, lines);
        Assert.Contains("Current model: claude-sonnet-4-6", output, StringComparison.Ordinal);
        Assert.Contains("Common aliases: sonnet, opus, haiku", output, StringComparison.Ordinal);
        Assert.Contains("Switched to: claude-opus-4-6", output, StringComparison.Ordinal);
        Assert.Contains(
            "Switching providers requires a new session. Restart with --provider openai --model gpt-4o.",
            output,
            StringComparison.Ordinal);
        Assert.Contains("Current effort: Balanced", output, StringComparison.Ordinal);
        Assert.Contains("Available effort levels: Fast, Balanced, Thorough", output, StringComparison.Ordinal);
        Assert.Contains("Switched effort to: Thorough", output, StringComparison.Ordinal);
        Assert.Contains("Switched effort to: Fast", output, StringComparison.Ordinal);
        Assert.Contains("Switched effort to: Balanced", output, StringComparison.Ordinal);
        Assert.Contains("Unknown effort: mystery", output, StringComparison.Ordinal);
        Assert.Contains("Current mode: Default", output, StringComparison.Ordinal);
        Assert.Contains("Switched permission mode to: Plan", output, StringComparison.Ordinal);
        Assert.Contains("Unknown mode: mystery", output, StringComparison.Ordinal);
        Assert.Contains("Current title: (none)", output, StringComparison.Ordinal);
        Assert.Contains("Session title set to: Sprint 7", output, StringComparison.Ordinal);
        Assert.Contains("Tags: (none)", output, StringComparison.Ordinal);
        Assert.Contains("Added tag: Alpha", output, StringComparison.Ordinal);
        Assert.Contains("Added tag: beta", output, StringComparison.Ordinal);
        Assert.Contains("Removed tag: alpha", output, StringComparison.Ordinal);
        Assert.Contains("Usage: /tag add <name>, /tag remove <name>, /tag clear", output, StringComparison.Ordinal);
        Assert.Contains("Session: session-1", output, StringComparison.Ordinal);
        Assert.Contains("Transcript: /tmp/session/transcript.jsonl", output, StringComparison.Ordinal);
        Assert.Contains("Title: Sprint 7", output, StringComparison.Ordinal);
        Assert.Contains("Tags: beta", output, StringComparison.Ordinal);
        Assert.Contains("Mode: Plan", output, StringComparison.Ordinal);
        Assert.Contains("Effort: Balanced", output, StringComparison.Ordinal);
        Assert.Contains("Auto-resume: queue", output, StringComparison.Ordinal);
        Assert.Contains("Session title cleared.", output, StringComparison.Ordinal);
        Assert.Contains("Cleared all session tags.", output, StringComparison.Ordinal);
        Assert.Null(bundle.Engine.SessionMetadata.Title);
        Assert.Empty(bundle.Engine.SessionMetadata.Tags);
        Assert.Equal(PermissionMode.Plan, bundle.Engine.SessionMetadata.Mode);
        Assert.Equal(QueryEffortLevel.Balanced, bundle.Engine.CurrentEffort);
    }

    [Fact]
    public async Task SessionCommand_UsesRuntimeAutoResumeModeWhenAvailable()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        var lines = new List<string>();
        var runtimeOptions = new AgentRuntimeOptions
        {
            AutoResumeMode = AgentAutoResumeMode.Disabled,
        };

        await new SessionCommand().ExecuteAsync(
            "",
            CreateContext(bundle.Engine, bundle.PermissionContext, lines, runtimeOptions: runtimeOptions));

        var output = string.Join(Environment.NewLine, lines);
        Assert.Contains("Auto-resume: disabled", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanCommand_EntersReportsStatusAndRestoresPreviousMode()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        var lines = new List<string>();
        await bundle.Engine.SetPermissionModeAsync(PermissionMode.Auto);
        var context = CreateContext(bundle.Engine, bundle.PermissionContext, lines);

        await new PlanCommand().ExecuteAsync("", context);
        await new PlanCommand().ExecuteAsync("status", context);
        await new PlanCommand().ExecuteAsync("exit", context);
        await new PlanCommand().ExecuteAsync("exit", context);

        var output = string.Join(Environment.NewLine, lines);
        Assert.Contains("Plan mode enabled. Available tools: ExitPlanMode, Read, Glob, Grep, WebFetch", output, StringComparison.Ordinal);
        Assert.Contains("Plan mode: active", output, StringComparison.Ordinal);
        Assert.Contains("Resume mode after approval: Auto", output, StringComparison.Ordinal);
        Assert.Contains("Plan mode disabled. Restored permission mode: Auto", output, StringComparison.Ordinal);
        Assert.Contains("Plan mode is not active.", output, StringComparison.Ordinal);
        Assert.False(bundle.Engine.IsPlanModeActive);
        Assert.Equal(PermissionMode.Auto, bundle.Engine.SessionMetadata.Mode);
    }

    [Fact]
    public async Task CompactCommand_ReportsUsageMissingHistoryAndSuccess()
    {
        using var temp = new TempDirectory();
        using var shortBundle = CreateEngineBundle(
            temp.Root,
            initialMessages:
            [
                UserMessage.FromText("one"),
                new AssistantMessage { Content = [new TextBlock("two")] },
            ]);
        var shortLines = new List<string>();
        var shortContext = CreateContext(shortBundle.Engine, shortBundle.PermissionContext, shortLines);

        await new CompactCommand().ExecuteAsync("nope", shortContext);
        await new CompactCommand().ExecuteAsync("2", shortContext);

        var shortOutput = string.Join(Environment.NewLine, shortLines);
        Assert.Contains("Usage: /compact [preserveTailCount]", shortOutput, StringComparison.Ordinal);
        Assert.Contains("Not enough history to compact yet.", shortOutput, StringComparison.Ordinal);

        using var successBundle = CreateEngineBundle(
            temp.FullPath("compact-success"),
            initialMessages:
            [
                UserMessage.FromText("one"),
                new AssistantMessage { Content = [new TextBlock("two")] },
                UserMessage.FromText("three"),
                new AssistantMessage { Content = [new TextBlock("four")] },
            ]);
        var successLines = new List<string>();

        await new CompactCommand().ExecuteAsync(
            "2",
            CreateContext(successBundle.Engine, successBundle.PermissionContext, successLines));

        Assert.Contains(
            "Compacted 2 messages and kept 2 recent messages in full.",
            string.Join(Environment.NewLine, successLines),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionMemoryAndPartialCompactCommands_ReportBoundaryAdjustments()
    {
        using var temp = new TempDirectory();
        var sessionMemoryMessages =
            new ConversationMessage[]
            {
                UserMessage.FromText("one"),
                new AssistantMessage { Content = [new TextBlock("two")] },
                UserMessage.FromText("three"),
                new AssistantMessage { Content = [new TextBlock("four")] },
                new AssistantMessage
                {
                    Content =
                    [
                        new ToolUseBlock
                        {
                            ToolUseId = "tool-1",
                            Name = "read",
                            Input = TestSupport.Json(new { path = "a.txt" }),
                        },
                    ],
                },
                UserMessage.FromToolResult("tool-1", "done"),
                new AssistantMessage { Content = [new TextBlock("tail-1")] },
                UserMessage.FromText("tail-2"),
            };
        var partialMessages =
            new ConversationMessage[]
            {
                UserMessage.FromText("prefix"),
                new AssistantMessage
                {
                    Content =
                    [
                        new ToolUseBlock
                        {
                            ToolUseId = "tool-1",
                            Name = "read",
                            Input = TestSupport.Json(new { path = "a.txt" }),
                        },
                    ],
                },
                UserMessage.FromToolResult("tool-1", "done"),
                new AssistantMessage { Content = [new TextBlock("tail-1")] },
                UserMessage.FromText("tail-2"),
            };

        using var memoryBundle = CreateEngineBundle(temp.FullPath("memory"), initialMessages: sessionMemoryMessages);
        var memoryLines = new List<string>();
        await new SessionMemoryCompactCommand().ExecuteAsync(
            "oops",
            CreateContext(memoryBundle.Engine, memoryBundle.PermissionContext, memoryLines));
        await new SessionMemoryCompactCommand().ExecuteAsync(
            "3",
            CreateContext(memoryBundle.Engine, memoryBundle.PermissionContext, memoryLines));

        var memoryOutput = string.Join(Environment.NewLine, memoryLines);
        Assert.Contains("Usage: /session-memory [preserveTailCount]", memoryOutput, StringComparison.Ordinal);
        Assert.Contains("Boundary adjusted from 5 to 4", memoryOutput, StringComparison.Ordinal);

        using var partialBundle = CreateEngineBundle(temp.FullPath("partial"), initialMessages: partialMessages);
        var partialLines = new List<string>();
        await new PartialCompactCommand().ExecuteAsync(
            "up_to nope",
            CreateContext(partialBundle.Engine, partialBundle.PermissionContext, partialLines));
        await new PartialCompactCommand().ExecuteAsync(
            "middle 2",
            CreateContext(partialBundle.Engine, partialBundle.PermissionContext, partialLines));
        await new PartialCompactCommand().ExecuteAsync(
            "up_to 2",
            CreateContext(partialBundle.Engine, partialBundle.PermissionContext, partialLines));

        var partialOutput = string.Join(Environment.NewLine, partialLines);
        Assert.Contains("Usage: /pcompact <up_to|from> <index>", partialOutput, StringComparison.Ordinal);
        Assert.Contains("Boundary adjusted from 2 to 1", partialOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MicrocompactCommand_ReportsUsageNoChangesAndSuccess()
    {
        using var temp = new TempDirectory();
        using var unchangedBundle = CreateEngineBundle(
            temp.FullPath("micro-none"),
            initialMessages:
            [
                UserMessage.FromText("tail"),
            ]);
        var unchangedLines = new List<string>();
        await new MicrocompactCommand().ExecuteAsync(
            "oops",
            CreateContext(unchangedBundle.Engine, unchangedBundle.PermissionContext, unchangedLines));
        await new MicrocompactCommand().ExecuteAsync(
            "1",
            CreateContext(unchangedBundle.Engine, unchangedBundle.PermissionContext, unchangedLines));

        var unchangedOutput = string.Join(Environment.NewLine, unchangedLines);
        Assert.Contains("Usage: /microcompact [preserveTailCount]", unchangedOutput, StringComparison.Ordinal);
        Assert.Contains("No old tool results or thinking blocks needed clearing.", unchangedOutput, StringComparison.Ordinal);

        var oldTimestamp = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        using var changedBundle = CreateEngineBundle(
            temp.FullPath("micro-change"),
            initialMessages:
            [
                UserMessage.FromToolResult("tool-1", "payload") with { Timestamp = oldTimestamp },
                new AssistantMessage
                {
                    Timestamp = oldTimestamp,
                    Content = [new ThinkingBlock("thinking"), new TextBlock("assistant text")],
                },
                UserMessage.FromText("tail"),
            ]);
        var changedLines = new List<string>();
        await new MicrocompactCommand().ExecuteAsync(
            "1",
            CreateContext(changedBundle.Engine, changedBundle.PermissionContext, changedLines));

        Assert.Contains(
            "Cleared 1 tool-result messages and 1 thinking blocks.",
            string.Join(Environment.NewLine, changedLines),
            StringComparison.Ordinal);
    }

    private static CommandContext CreateContext(
        QueryEngine engine,
        PermissionContext permissionContext,
        List<string> lines,
        IReadOnlyList<ICommand>? commands = null,
        AgentRuntimeOptions? runtimeOptions = null,
        AiProvider aiProvider = AiProvider.Anthropic,
        ToolRegistry? tools = null) =>
        new()
        {
            WriteLine = lines.Add,
            Tools = tools ?? new ToolRegistry(),
            QueryEngine = engine,
            AiProvider = aiProvider,
            PermissionContext = permissionContext,
            AgentTaskRuntime = new InMemoryAgentTaskRuntime(),
            AgentRuntimeOptions = runtimeOptions,
            Commands = commands ?? [],
            CancellationToken = CancellationToken.None,
        };

    private static EngineBundle CreateEngineBundle(
        string workingDirectory,
        IReadOnlyList<ConversationMessage>? initialMessages = null,
        TokenUsage? initialUsage = null)
    {
        var permissionContext = new PermissionContext
        {
            WorkingDirectory = workingDirectory,
        };
        var journal = new RecordingJournal();
        var provider = new ContextProvider
        {
            WorkingDirectory = workingDirectory,
            PermissionContext = permissionContext,
        };

        var engine = TestSupport.CreateQueryEngine(
            TestSupport.CreateChatClient(new FakeAnthropicHandler()),
            new ToolRegistry(),
            provider,
            new DefaultPermissionChecker(),
            new QueryEngineConfig
            {
                EnableAutoCompact = false,
            },
            journal: journal,
            initialMessages: initialMessages,
            initialUsage: initialUsage);

        return new EngineBundle(engine, journal, permissionContext);
    }

    private static async Task<string> ExecutePromptCommandAsync(ICommand command, string args = "")
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        var lines = new List<string>();

        await command.ExecuteAsync(args, CreateContext(bundle.Engine, bundle.PermissionContext, lines));

        var promptMessage = Assert.Single(bundle.Engine.Messages.OfType<UserMessage>());
        return Assert.IsType<TextBlock>(Assert.Single(promptMessage.Content)).Text;
    }

    private static CommandRegistry BuildCliCommandRegistry()
    {
        var cliAssembly = AppDomain.CurrentDomain.GetAssemblies()
                              .FirstOrDefault(assembly => assembly.GetName().Name == "Aexon.Cli")
                          ?? Assembly.Load("Aexon.Cli");
        var programType = cliAssembly.GetType("Aexon.Cli.Program", throwOnError: true)!;
        var buildMethod = programType.GetMethod("BuildCommandRegistry", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);

        return Assert.IsType<CommandRegistry>(buildMethod!.Invoke(null, null));
    }

    private sealed record EngineBundle(
        QueryEngine Engine,
        RecordingJournal Journal,
        PermissionContext PermissionContext) : IDisposable
    {
        public void Dispose() => Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
