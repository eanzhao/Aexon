using System.Text;
using System.Text.Json;
using Aexon.Commands;
using Aexon.Core.Agents;
using Aexon.Core.AppState;
using Aexon.Core.Channels;
using Aexon.Core.Commands;
using Aexon.Core.Configuration;
using Aexon.Core.Context;
using Aexon.Core.Cron;
using Aexon.Core.Hooks;
using Aexon.Core.Mcp;
using Aexon.Core.Memory;
using Aexon.Core.Permissions;
using Aexon.Core.Providers;
using Aexon.Core.Query;
using Aexon.Core.Storage;
using Aexon.Core.Todos;
using Aexon.Core.Tools;
using Aexon.Tools;
using Microsoft.Extensions.AI;

namespace Aexon.Cli;

/// <summary>
/// Hosts the Aexon CLI entry point.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = CliOptions.Parse(args);
        if (!string.IsNullOrWhiteSpace(options.ParseError))
        {
            Console.Error.WriteLine(options.ParseError);
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.ContinueLatest && !string.IsNullOrWhiteSpace(options.ResumeTarget))
        {
            Console.Error.WriteLine("--continue 和 --resume 不能同时使用。");
            return 1;
        }

        if (options.ForkSession &&
            !options.ContinueLatest &&
            string.IsNullOrWhiteSpace(options.ResumeTarget))
        {
            Console.Error.WriteLine("--fork-session 只能和 --continue 或 --resume 一起使用。");
            return 1;
        }

        var transcriptStore = new JsonlTranscriptStore();
        var resumeLoader = new SessionResumeLoader(transcriptStore, new ConversationRecovery());
        var restorePipeline = new SessionRestorePipeline();

        ProcessedResume? resumed = null;
        if (options.ContinueLatest || !string.IsNullOrWhiteSpace(options.ResumeTarget))
        {
            var source = options.ContinueLatest
                ? ResumeSource.Latest()
                : ResumeSource.Session(options.ResumeTarget!);

            var loadResult = await resumeLoader.LoadAsync(source);
            if (loadResult == null)
            {
                Console.Error.WriteLine("没有找到可恢复的会话。");
                return 1;
            }

            resumed = await restorePipeline.RestoreAsync(
                loadResult,
                new ResumeOptions
                {
                    WorkingDirectoryOverride = options.WorkingDirectory,
                    ModelOverride = string.IsNullOrWhiteSpace(options.Model)
                        ? null
                        : options.Model.Trim(),
                    ForkSession = options.ForkSession,
                });
        }

        var workingDirectory = Path.GetFullPath(
            resumed?.WorkingDirectory ??
            (string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Environment.CurrentDirectory
                : options.WorkingDirectory));

        Environment.CurrentDirectory = workingDirectory;

        var contextProvider = new ContextProvider
        {
            WorkingDirectory = workingDirectory,
        };
        if (resumed?.Metadata.Mode is PermissionMode resumedMode)
            contextProvider.PermissionContext.Mode = resumedMode;
        await contextProvider.LoadMemoryAsync();
        var rawAnthropicSettings = AnthropicClientSettingsLoader.Load(
            workingDirectory,
            appBaseDirectory: AppContext.BaseDirectory);

        var configuredAnthropicModel =
            resumed == null &&
            string.IsNullOrWhiteSpace(options.Model) &&
            !string.IsNullOrWhiteSpace(rawAnthropicSettings.Model)
                ? rawAnthropicSettings.Model
                : null;
        var sessionTarget = AiProviderSelection.ResolveSessionTarget(
            options.Provider,
            options.Model ?? configuredAnthropicModel,
            resumed?.SourceSession.Provider,
            resumed?.Model);
        var aiProvider = sessionTarget.Provider;
        var model = sessionTarget.Model;
        var config = new QueryEngineConfig
        {
            Provider = aiProvider,
            Model = model,
            UseStreamingApi = true,
            MaxTurns = options.MaxTurns ?? 50,
        };
        if (aiProvider == AiProvider.Anthropic &&
            rawAnthropicSettings.MaxTokens is { } configuredMaxTokens)
        {
            config.MaxTokens = configuredMaxTokens;
        }
        var providerRouter = new DefaultProviderCapabilityRouter();
        var managedSettings = ManagedSettingsLoader.Load(
            workingDirectory,
            options.SettingsPath);
        var managedPolicy = ManagedRuntimePolicy.Resolve(
            managedSettings.Settings,
            rawAnthropicSettings,
            providerRouter.Resolve(model));
        var anthropicSettings = managedPolicy.AnthropicSettings;
        using var chatClientRuntime = ChatClientRuntime.Create(
            aiProvider,
            model,
            config,
            anthropicSettings);
        var chatClient = chatClientRuntime.ChatClient;
        var agentSettings = AgentSettingsLoader.Load(
            workingDirectory,
            options.SettingsPath);
        var agentRuntimeOptions = new AgentRuntimeOptions
        {
            AutoResumeMode = agentSettings.Settings.AutoResumeMode,
        };
        var hookBuild = managedPolicy.AllowPlugins
            ? HookRuntimeBuilder.Build(
                workingDirectory,
                options.SettingsPath)
            : new HookRuntimeBuildResult(
                new HookRuntime(),
                ["Hooks: disabled by organization policy."],
                [],
                0);
        var hooks = hookBuild.Runtime;

        var session = resumed != null && resumed.ContinueExistingSession
            ? resumed.SourceSession
            : await transcriptStore.CreateSessionAsync(workingDirectory, model);

        session.WorkingDirectory = workingDirectory;
        session.Model = model;
        session.Provider = AiProviderSelection.ToStorageValue(aiProvider);
        session.Metadata = resumed?.Metadata.Clone() ?? session.Metadata;
        await transcriptStore.UpdateSessionAsync(session);
        var journal = new ConversationJournal(transcriptStore, session);
        var memoryLayout = CreateMemdirLayout(workingDirectory);
        memoryLayout.EnsureDirectories();
        var sessionMemoryFile = memoryLayout.CreateSessionMemoryFile(session.SessionId);
        contextProvider.SessionMemoryContent = await sessionMemoryFile.LoadAsync();
        var memoryConsolidationService = new MemoryConsolidationService(
            memoryLayout,
            ResolveMemoryTeamName(managedSettings.Settings));
        hooks.Register(new SessionMemoryConsolidationHookObserver(memoryConsolidationService));
        if (resumed != null && !resumed.ContinueExistingSession)
        {
            await journal.SeedAsync(
                resumed.Messages,
                resumed.Metadata,
                workingDirectory,
                model);
        }

        var metadataEntries = resumed?.ContinueExistingSession == true
            ? (await transcriptStore.LoadProjectionAsync(
                session,
                new TranscriptLoadOptions())).MetadataEntries
            : [];
        var todoRuntime = await PersistentTodoRuntime.CreateAsync(
            journal,
            metadataEntries);
        var cronRuntime = await PersistentCronRuntime.CreateAsync(
            journal,
            metadataEntries);
        var agentTaskRuntime = await PersistentAgentTaskRuntime.CreateAsync(
            journal,
            metadataEntries,
            autoPrunePolicy: agentSettings.Settings.BuildRetentionPolicy());
        var agentTeamRuntime = await PersistentAgentTeamRuntime.CreateAsync(
            journal,
            metadataEntries);
        var agentMessageRuntime = await PersistentAgentMessageRuntime.CreateAsync(
            journal,
            metadataEntries);
        var channelConnectionManager = new ChannelConnectionManager();
        var channelRouter = new ChannelRouter(
            new BridgeChannelTransport(channelConnectionManager, agentMessageRuntime),
            new UdsChannelTransport(channelConnectionManager, agentMessageRuntime));
        var messageActivationRuntime = new InMemoryAgentMessageActivationRuntime();
        var toolRegistry = BuildToolRegistry(
            providerRouter,
            managedPolicy.AllowWebSearch,
            () => config.Model,
            () => config.Provider,
            chatClient,
            hooks,
            agentTaskRuntime,
            agentTeamRuntime,
            agentMessageRuntime,
            todoRuntime,
            cronRuntime,
            channelRouter,
            messageActivationRuntime,
            agentRuntimeOptions,
            agentSettings.Settings.BackgroundRunConcurrency);
        var commandRegistry = BuildCommandRegistry();
        await using var mcpRuntime = managedPolicy.AllowExternalMcpServers
            ? await McpRuntime.CreateAsync(
                toolRegistry,
                workingDirectory,
                options.SettingsPath)
            : new McpRuntime();
        toolRegistry.RegisterDeferred(new DeferredToolRegistration(
            "ListMcpResources",
            () => new ListMcpResourcesTool(mcpRuntime.ConnectionManager),
            Aliases: ["ListMcpResourcesTool"],
            Keywords: ["mcp", "resource", "resources", "list", "uri", "server"]));
        toolRegistry.RegisterDeferred(new DeferredToolRegistration(
            "ReadMcpResource",
            () => new ReadMcpResourceTool(mcpRuntime.ConnectionManager),
            Aliases: ["ReadMcpResourceTool"],
            Keywords: ["mcp", "resource", "read", "uri", "server", "contents"]));

        await using var queryEngine = new QueryEngine(
            chatClient,
            toolRegistry,
            new DefaultPermissionChecker(),
            config,
            contextProvider,
            hooks: hooks,
            journal: journal,
            sessionMemoryFile: sessionMemoryFile,
            initialMessages: resumed?.Messages,
            initialUsage: resumed?.TotalUsage,
            initialMetadata: resumed?.Metadata,
            askUserQuestion: PromptUserQuestionAsync);
        cronRuntime.SetMessageSink(queryEngine.EnqueueExternalMessageAsync);
        agentTaskRuntime.SetMessageSink(queryEngine.EnqueueExternalMessageAsync);
        channelConnectionManager.SetMessageSink(queryEngine.EnqueueExternalMessageAsync);
        await using var cronScheduler = new CronScheduler(cronRuntime, workingDirectory);
        toolRegistry.Register(new EnterPlanModeTool(queryEngine));
        toolRegistry.Register(new ExitPlanModeTool(queryEngine));
        var permissionContext = contextProvider.GetPermissionContext();
        var appStateStore = new AppStateStore();
        var appStateBridge = new AppStateHostBridge(
            appStateStore,
            new JsonFileAppStateBoundary(CreateAppStatePath()));
        var appStateProjector = new AppStateProjector();
        AgentMailboxTaskProjector.Synchronize(agentMessageRuntime, agentTaskRuntime);

        async Task PublishAppStateAsync()
        {
            try
            {
                appStateStore.Reset(appStateProjector.CreateSnapshot(
                    workingDirectory,
                    permissionContext.Mode,
                    agentRuntimeOptions.AutoResumeMode,
                    sessionId: session.SessionId,
                    memoryRootDirectory: memoryLayout.ProjectMemoryDirectory,
                    managedSettings: managedSettings.Settings,
                    activeTokenSource: managedPolicy.ActiveTokenSource,
                    mcpConnectionManager: mcpRuntime.ConnectionManager,
                    channelConnectionManager: channelConnectionManager,
                    agentTaskRuntime: agentTaskRuntime,
                    agentMessageRuntime: agentMessageRuntime,
                    agentTeamRuntime: agentTeamRuntime,
                    agentRuntimeOptions: agentRuntimeOptions,
                    todoRuntime: todoRuntime,
                    awayModeController: queryEngine));
                await appStateBridge.PublishAsync();
            }
            catch
            {
                // App-state publishing is best effort and should never break the CLI loop.
            }
        }

        await PublishAppStateAsync();

        var startupParts = new List<string>();
        switch (resumed)
        {
            case { ContinueExistingSession: true }:
                startupParts.Add(
                    $"Resumed session {resumed.SourceSession.SessionId} ({resumed.Messages.Count} messages)");
                break;

            case not null:
                startupParts.Add(
                    $"Forked session {session.SessionId} from {resumed.SourceSession.SessionId} ({resumed.Messages.Count} messages)");
                break;
        }

        if (!string.IsNullOrWhiteSpace(mcpRuntime.StartupSummary))
            startupParts.Add(mcpRuntime.StartupSummary);
        if (!string.IsNullOrWhiteSpace(hookBuild.StartupSummary))
            startupParts.Add(hookBuild.StartupSummary);
        if (!string.IsNullOrWhiteSpace(agentSettings.StartupSummary))
            startupParts.Add(agentSettings.StartupSummary);
        if (!string.IsNullOrWhiteSpace(managedSettings.StartupSummary))
            startupParts.Add(managedSettings.StartupSummary);
        if (!string.IsNullOrWhiteSpace(managedPolicy.StartupSummary))
            startupParts.Add(managedPolicy.StartupSummary);
        if (!string.IsNullOrWhiteSpace(chatClientRuntime.StartupSummary))
            startupParts.Add(chatClientRuntime.StartupSummary);

        var startupNote = startupParts.Count == 0
            ? null
            : string.Join(Environment.NewLine, startupParts);

        var shell = new AexonShell(
            workingDirectory,
            chatClientRuntime.HasRequiredConfiguration,
            toolRegistry,
            commandRegistry,
            queryEngine,
            aiProvider,
            permissionContext,
            agentTaskRuntime,
            agentTeamRuntime,
            agentMessageRuntime,
            messageActivationRuntime,
            agentRuntimeOptions,
            startupNote,
            PublishAppStateAsync);

        NonInteractiveRunOptions? nonInteractiveOptions = null;
        if (options.PrintMode)
        {
            var stdinContent = Console.IsInputRedirected
                ? await Console.In.ReadToEndAsync()
                : null;
            var prompt = NonInteractivePromptBuilder.Compose(options.InitialPrompt, stdinContent);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Console.Error.WriteLine("--print 需要命令行 prompt 或 stdin 输入。");
                return 1;
            }

            nonInteractiveOptions = new NonInteractiveRunOptions(
                prompt,
                options.OutputFormat,
                options.ApprovalMode);
        }

        var exitCode = nonInteractiveOptions != null
            ? await shell.RunNonInteractiveAsync(nonInteractiveOptions)
            : await shell.RunAsync(options.InitialPrompt);
        await PublishAppStateAsync();
        return exitCode;
    }

    private static ToolRegistry BuildToolRegistry(
        IProviderCapabilityRouter providerRouter,
        bool allowWebSearch,
        Func<string?> currentModelAccessor,
        Func<AiProvider> currentProviderAccessor,
        IChatClient chatClient,
        IHookRuntime hooks,
        IAgentTaskRuntime agentTaskRuntime,
        IAgentTeamRuntime agentTeamRuntime,
        IAgentMessageRuntime agentMessageRuntime,
        ITodoRuntime todoRuntime,
        ICronRuntime cronRuntime,
        ChannelRouter channelRouter,
        IAgentMessageActivationRuntime messageActivationRuntime,
        AgentRuntimeOptions agentRuntimeOptions,
        int backgroundRunConcurrency)
    {
        var registry = new ToolRegistry();
        var backgroundRunScheduler = new BackgroundAgentRunScheduler(
            maxConcurrency: backgroundRunConcurrency);
        registry.Register(new ToolSearchTool(registry));
        registry.Register(new BashTool());
        registry.Register(new FileReadTool());
        registry.Register(new FileWriteTool());
        registry.Register(new FileEditTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new AskUserQuestionTool());
        registry.Register(new TodoWriteTool(todoRuntime));
        registry.Register(new WebFetchTool());
        registry.Register(new AgentTool(
            new QueryEngineAgentRunner(chatClient, hooks: hooks),
            providerRouter,
            agentTaskRuntime,
            agentTeamRuntime,
            agentMessageRuntime,
            hooks,
            backgroundRunScheduler,
            messageActivationRuntime,
            runtimeOptions: agentRuntimeOptions));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "CronCreate",
            () => new CronCreateTool(cronRuntime),
            Aliases: ["CronCreateTool"],
            Keywords: ["cron", "schedule", "scheduled", "job", "timer"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "CronDelete",
            () => new CronDeleteTool(cronRuntime),
            Aliases: ["CronDeleteTool"],
            Keywords: ["cron", "schedule", "scheduled", "job", "remove"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "CronList",
            () => new CronListTool(cronRuntime),
            Aliases: ["CronListTool"],
            Keywords: ["cron", "schedule", "scheduled", "job", "list"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TeamCreate",
            () => new TeamCreateTool(agentTeamRuntime),
            Keywords: ["team", "teammate", "squad", "group", "parallel"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TeamStatus",
            () => new TeamStatusTool(agentTeamRuntime),
            Keywords: ["team", "teammate", "status", "group", "parallel"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TeamDissolve",
            () => new TeamDissolveTool(agentTeamRuntime),
            Keywords: ["team", "teammate", "dissolve", "group", "cleanup"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "SendMessage",
            () => new SendMessageTool(
                agentMessageRuntime,
                agentTeamRuntime,
                messageActivationRuntime,
                agentTaskRuntime,
                channelRouter: channelRouter),
            Keywords: ["message", "mailbox", "send", "notify", "reply"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "MailboxStatus",
            () => new MailboxStatusTool(agentMessageRuntime),
            Keywords: ["mailbox", "message", "inbox", "outbox", "status"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "MailboxRespond",
            () => new MailboxRespondTool(agentMessageRuntime, messageActivationRuntime, agentTaskRuntime),
            Keywords: ["mailbox", "message", "respond", "reply", "inbox"]));
        if (allowWebSearch)
        {
            registry.RegisterDeferred(new DeferredToolRegistration(
                "WebSearch",
                () => new WebSearchTool(providerRouter, currentModelAccessor, currentProviderAccessor),
                Keywords: ["web", "search", "internet", "google", "news"]));
        }
        registry.Register(new AgentStatusTool(agentTaskRuntime));
        registry.Register(new AgentResumeTool(agentTaskRuntime, agentMessageRuntime, messageActivationRuntime));
        registry.Register(new AgentStopTool(agentTaskRuntime));
        registry.Register(new AgentWaitTool(agentTaskRuntime));
        return registry;
    }

    private static MemdirLayout CreateMemdirLayout(string workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var memoryBaseDirectory = Path.Combine(home, ".aexon", "memory");
        return new MemdirLayout
        {
            MemoryBaseDirectory = memoryBaseDirectory,
            ProjectRootDirectory = workingDirectory,
        };
    }

    private static string CreateAppStatePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aexon", "state", "current.json");
    }

    private static string ResolveMemoryTeamName(ManagedSettingsSnapshot settings)
    {
        var policy = settings.OrganizationPolicy;
        if (!string.IsNullOrWhiteSpace(policy.WorkspaceId))
            return policy.WorkspaceId;

        if (!string.IsNullOrWhiteSpace(policy.OrganizationId))
            return policy.OrganizationId;

        return "default";
    }

    private static Task<UserQuestionResponse> PromptUserQuestionAsync(
        UserQuestionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Console.IsInputRedirected)
            throw new InvalidOperationException("当前会话不是交互式输入，无法向用户提问。");

        Console.WriteLine();
        Console.WriteLine($"[提问] {request.Question}");

        if (request.Options is { Count: > 0 } options)
            return Task.FromResult(PromptForOptionSelection(options));

        return Task.FromResult(new UserQuestionResponse(ReadRequiredAnswer("> ")));
    }

    private static UserQuestionResponse PromptForOptionSelection(IReadOnlyList<string> options)
    {
        for (var i = 0; i < options.Count; i++)
            Console.WriteLine($"{i + 1}. {options[i]}");

        while (true)
        {
            var answer = ReadRequiredAnswer("请选择编号或直接输入选项内容: ");
            if (int.TryParse(answer, out var index) &&
                index >= 1 &&
                index <= options.Count)
            {
                return new UserQuestionResponse(options[index - 1]);
            }

            var matched = options.FirstOrDefault(
                option => string.Equals(option, answer, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
                return new UserQuestionResponse(matched);

            Console.WriteLine("输入无效，请重新选择。");
        }
    }

    private static string ReadRequiredAnswer(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            var answer = Console.ReadLine();
            if (answer == null)
                throw new InvalidOperationException("用户输入已结束，无法继续等待回答。");

            answer = answer.Trim();
            if (!string.IsNullOrWhiteSpace(answer))
                return answer;

            Console.WriteLine("请输入内容。");
        }
    }

    private static CommandRegistry BuildCommandRegistry()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommand());
        registry.Register(new ClearCommand());
        registry.Register(new CompactCommand());
        registry.Register(new CostCommand());
        registry.Register(new EffortCommand());
        registry.Register(new ExitCommand());
        registry.Register(new FastCommand());
        registry.Register(new AgentsCommand());
        registry.Register(new MailboxCommand());
        registry.Register(new ModelCommand());
        registry.Register(new MicrocompactCommand());
        registry.Register(new ModeCommand());
        registry.Register(new PlanCommand());
        registry.Register(new PartialCompactCommand());
        registry.Register(new SessionCommand());
        registry.Register(new SessionMemoryCompactCommand());
        registry.Register(new TeamCommand());
        registry.Register(new TitleCommand());
        registry.Register(new TagCommand());
        registry.Register(new AwayCommand());
        return registry;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Usage:
  aexon [--cwd <path>] [--model <model>] [--provider <name>] [--resume <session>] [--continue] [--fork-session] [--settings <path>] [prompt]
  aexon --print [-p] [--output-format <text|markdown|json>] [--approval-mode <allow|deny>] [--max-turns <n>] [prompt]

Options:
  --cwd <path>       Working directory for this session
  --model <model>    Main model or alias (sonnet / opus / haiku / gpt-4o / o3 / ...)
  --provider <name>  AI provider: anthropic (default), openai, or ollama
  --resume <id>      Resume a specific session by id, directory, manifest, or transcript path
  --continue         Resume the most recently updated session
  --fork-session     Fork the resumed transcript into a brand new session
  --settings <path>  Load hooks and MCP servers from a specific settings.json file
  --mcp-config       Alias for --settings
  --print, -p        Run a single non-interactive prompt and exit
  --output-format    Non-interactive output format: text, markdown, or json
  --approval-mode    Non-interactive permission policy: allow or deny (default)
  --max-turns <n>    Maximum assistant/tool turns for this run
  --help             Show this help

Environment:
  ANTHROPIC_API_KEY  API key for Anthropic Claude models
  OPENAI_API_KEY     API key for OpenAI models
  OPENAI_BASE_URL    Custom base URL for OpenAI-compatible endpoints
  OLLAMA_HOST        Base URL for the Ollama server (default: http://127.0.0.1:11434)
  OLLAMA_BASE_URL    Alias for OLLAMA_HOST
  AEXON_CHAT_LOGGING Enable MEAI pipeline logging on the console (1/true)
""");
    }

    private sealed class AexonShell
    {
        private readonly string _workingDirectory;
        private readonly bool _hasProviderConfiguration;
        private readonly ToolRegistry _toolRegistry;
        private readonly CommandRegistry _commandRegistry;
        private readonly QueryEngine _queryEngine;
        private readonly AiProvider _aiProvider;
        private readonly PermissionContext _permissionContext;
        private readonly IAgentTaskRuntime _agentTaskRuntime;
        private readonly IAgentTeamRuntime _agentTeamRuntime;
        private readonly IAgentMessageRuntime _agentMessageRuntime;
        private readonly IAgentMessageActivationRuntime _agentMessageActivationRuntime;
        private readonly AgentRuntimeOptions _agentRuntimeOptions;
        private readonly string? _startupNote;
        private readonly Func<Task>? _afterInputAsync;
        private bool _exitRequested;

        public AexonShell(
            string workingDirectory,
            bool hasProviderConfiguration,
            ToolRegistry toolRegistry,
            CommandRegistry commandRegistry,
            QueryEngine queryEngine,
            AiProvider aiProvider,
            PermissionContext permissionContext,
            IAgentTaskRuntime agentTaskRuntime,
            IAgentTeamRuntime agentTeamRuntime,
            IAgentMessageRuntime agentMessageRuntime,
            IAgentMessageActivationRuntime agentMessageActivationRuntime,
            AgentRuntimeOptions agentRuntimeOptions,
            string? startupNote,
            Func<Task>? afterInputAsync = null)
        {
            _workingDirectory = workingDirectory;
            _hasProviderConfiguration = hasProviderConfiguration;
            _toolRegistry = toolRegistry;
            _commandRegistry = commandRegistry;
            _queryEngine = queryEngine;
            _aiProvider = aiProvider;
            _permissionContext = permissionContext;
            _agentTaskRuntime = agentTaskRuntime;
            _agentTeamRuntime = agentTeamRuntime;
            _agentMessageRuntime = agentMessageRuntime;
            _agentMessageActivationRuntime = agentMessageActivationRuntime;
            _agentRuntimeOptions = agentRuntimeOptions;
            _startupNote = startupNote;
            _afterInputAsync = afterInputAsync;
        }

        public async Task<int> RunAsync(string? initialPrompt)
        {
            var interactive = !Console.IsInputRedirected && string.IsNullOrWhiteSpace(initialPrompt);
            if (interactive)
                PrintBanner();

            var commandContext = new CommandContext
            {
                WriteLine = Console.WriteLine,
                Tools = _toolRegistry,
                QueryEngine = _queryEngine,
                AiProvider = _aiProvider,
                PermissionContext = _permissionContext,
                AgentTaskRuntime = _agentTaskRuntime,
                AgentAutoResumeMode = _agentRuntimeOptions.AutoResumeMode,
                AgentRuntimeOptions = _agentRuntimeOptions,
                AgentTeamRuntime = _agentTeamRuntime,
                AgentMessageRuntime = _agentMessageRuntime,
                AgentMessageActivationRuntime = _agentMessageActivationRuntime,
                Commands = _commandRegistry.GetAll(),
                DelayAsync = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                CancellationToken = CancellationToken.None,
                RequestExit = () => _exitRequested = true,
                RequestClear = () =>
                {
                    if (!Console.IsOutputRedirected)
                        Console.Clear();
                },
            };

            if (!string.IsNullOrWhiteSpace(initialPrompt))
            {
                await HandleInputAsync(initialPrompt, commandContext);
                return 0;
            }

            while (!_exitRequested)
            {
                if (interactive)
                    Console.Write("\nclaudesharp> ");

                var input = Console.ReadLine();
                if (input == null)
                    break;

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                await HandleInputAsync(input, commandContext);
            }

            return 0;
        }

        public async Task<int> RunNonInteractiveAsync(NonInteractiveRunOptions options)
        {
            var result = await ExecuteNonInteractiveQueryAsync(options);

            switch (options.OutputFormat)
            {
                case NonInteractiveOutputFormat.Json:
                    Console.WriteLine(result.ToJson());
                    break;
                case NonInteractiveOutputFormat.Markdown:
                case NonInteractiveOutputFormat.Text:
                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        Console.Write(result.Output);
                        if (!result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                            Console.WriteLine();
                    }

                    if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                        Console.Error.WriteLine(result.ErrorMessage);
                    break;
            }

            return result.ExitCode;
        }

        private async Task HandleInputAsync(string input, CommandContext commandContext)
        {
            try
            {
                if (_commandRegistry.IsCommand(input))
                {
                    var parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
                    var name = parts[0];
                    var args = parts.Length > 1 ? parts[1] : "";
                    var command = _commandRegistry.Get(name);
                    if (command != null)
                        await command.ExecuteAsync(args, commandContext);
                    return;
                }

                await RunQueryAsync(input);
            }
            finally
            {
                if (_afterInputAsync != null)
                    await _afterInputAsync();
            }
        }

        private async Task RunQueryAsync(string input)
        {
            var wroteAssistantText = false;

            await foreach (var evt in _queryEngine.SubmitMessageAsync(input))
            {
                switch (evt)
                {
                    case TextDeltaEvent text:
                        wroteAssistantText = true;
                        Console.Write(text.Text);
                        break;

                    case ThinkingDeltaEvent:
                        // Hide raw thinking text for now to keep the terminal output clean.
                        break;

                    case ToolUseStartEvent toolUse:
                        if (wroteAssistantText)
                            Console.WriteLine();

                        Console.WriteLine(
                            $"\n[{toolUse.ToolName}] {SummarizeToolInput(toolUse.Input)}");
                        break;

                    case PermissionRequestEvent permissionRequest:
                        permissionRequest.SetResponse(AskForPermission(permissionRequest));
                        break;

                    case ToolResultEvent toolResult:
                        var status = toolResult.IsError ? "failed" : "done";
                        Console.WriteLine($"[{toolResult.ToolName}] {status}");
                        if (toolResult.IsError)
                            Console.WriteLine(toolResult.Result);
                        break;

                    case ContextCompactionEvent compaction:
                        var prefix = compaction.Automatic ? "[context:auto]" : "[context]";
                        if (compaction.Mode == "microcompact")
                        {
                            Console.WriteLine(
                                $"{prefix} microcompact: cleared {compaction.ClearedToolResults} tool results, {compaction.ClearedThinkingBlocks} thinking blocks");
                        }
                        else if (compaction.Mode == "session_memory")
                        {
                            Console.WriteLine(
                                $"{prefix} session memory: folded {compaction.RemovedMessages} older messages and kept {compaction.PreservedMessages} messages verbatim");
                        }
                        else if (compaction.Mode == "compact")
                        {
                            Console.WriteLine(
                                $"{prefix} compact: summarized {compaction.RemovedMessages} messages and kept {compaction.PreservedMessages} messages active");
                        }
                        else if (compaction.Mode == "failed")
                        {
                            Console.WriteLine($"{prefix} failed: {compaction.Reason}");
                        }
                        else if (compaction.Mode == "skipped")
                        {
                            Console.WriteLine($"{prefix} skipped: {compaction.Reason}");
                        }
                        break;

                    case MessageEndEvent:
                        if (wroteAssistantText)
                            Console.WriteLine();
                        break;

                    case PromptCacheStatusEvent cacheStatus when cacheStatus.BreakDetected:
                        if (cacheStatus.Usage.CacheCreationInputTokens > 0)
                        {
                            Console.WriteLine(
                                $"[cache] prompt cache 断了：这次没命中旧缓存，已按当前前缀重建 {cacheStatus.Usage.CacheCreationInputTokens:N0} 个 token。");
                        }
                        else
                        {
                            Console.WriteLine(
                                "[cache] prompt cache 断了：这次没命中旧缓存，可能是前缀变化、缓存过期，或当前上下文还没达到缓存门槛。");
                        }
                        break;

                    case QueryCompleteEvent complete when !complete.Success:
                        Console.WriteLine();
                        Console.WriteLine($"请求失败: {complete.ErrorMessage}");
                        break;
                }
            }
        }

        private async Task<NonInteractiveRunResult> ExecuteNonInteractiveQueryAsync(
            NonInteractiveRunOptions options)
        {
            var assistantText = new StringBuilder();
            var permissionDenied = false;
            QueryCompleteEvent? completion = null;

            await foreach (var evt in _queryEngine.SubmitMessageAsync(options.Prompt))
            {
                switch (evt)
                {
                    case TextDeltaEvent text:
                        assistantText.Append(text.Text);
                        break;

                    case PermissionRequestEvent permissionRequest:
                        var approved = options.ApprovalMode == NonInteractiveApprovalMode.Allow;
                        if (!approved)
                            permissionDenied = true;

                        permissionRequest.SetResponse(approved);
                        break;

                    case ToolResultEvent toolResult when
                        toolResult.IsError &&
                        LooksLikePermissionError(toolResult.Result):
                        permissionDenied = true;
                        break;

                    case QueryCompleteEvent complete:
                        completion = complete;
                        break;
                }
            }

            completion ??= new QueryCompleteEvent
            {
                Success = !permissionDenied,
                Duration = TimeSpan.Zero,
                TurnCount = 0,
                TotalUsage = _queryEngine.TotalUsage,
                ErrorMessage = permissionDenied
                    ? "Permission denied."
                    : "Query ended without a completion event.",
            };

            var success = completion.Success && !permissionDenied;
            var errorMessage = permissionDenied
                ? completion.ErrorMessage ?? "Permission denied."
                : completion.ErrorMessage;

            return new NonInteractiveRunResult(
                success,
                assistantText.ToString(),
                errorMessage,
                permissionDenied,
                completion.TurnCount,
                completion.Duration,
                completion.TotalUsage);
        }

        private bool AskForPermission(PermissionRequestEvent request)
        {
            if (Console.IsInputRedirected)
                return false;

            Console.Write($"{request.Description}，是否允许？ [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            return answer is "y" or "Y" or "yes" or "YES";
        }

        private static string SummarizeToolInput(JsonElement input)
        {
            var raw = input.GetRawText();
            return raw.Length <= 120 ? raw : $"{raw[..117]}...";
        }

        private static bool LooksLikePermissionError(string result) =>
            result.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("denied by rule", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("requires confirmation", StringComparison.OrdinalIgnoreCase);

        private void PrintBanner()
        {
            Console.WriteLine("Aexon (.NET 10)");
            Console.WriteLine($"Working directory: {_workingDirectory}");
            Console.WriteLine($"Model: {_queryEngine.CurrentModel}");
            if (!string.IsNullOrWhiteSpace(_queryEngine.SessionId))
                Console.WriteLine($"Session: {_queryEngine.SessionId}");
            if (!string.IsNullOrWhiteSpace(_startupNote))
                Console.WriteLine(_startupNote);
            Console.WriteLine("输入 /help 查看内置命令，/exit 退出。");

            if (!_hasProviderConfiguration)
            {
                Console.WriteLine(
                    "未检测到当前 provider 的可用连接配置。你仍然可以使用本地斜杠命令，但发起 AI 请求会失败。");
            }
        }
    }
}
