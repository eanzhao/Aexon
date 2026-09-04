using System.Reflection;
using System.Text;
using System.Text.Json;
using Aexon.Commands;
using Aexon.Core.Agents;
using Aexon.Core.AppState;
using Aexon.Core.Auth;
using Aexon.Core.Channels;
using Aexon.Core.Commands;
using Aexon.Core.Configuration;
using Aexon.Core.Context;
using Aexon.Core.Cron;
using Aexon.Core.Hooks;
using Aexon.Core.Markdown;
using Aexon.Core.Mcp;
using Aexon.Core.Memory;
using Aexon.Core.Permissions;
using Aexon.Core.Providers;
using Aexon.Core.Query;
using Aexon.Core.Skills;
using Aexon.Core.Storage;
using Aexon.Core.Todos;
using Aexon.Core.Tools;
using Aexon.Tools;
using Microsoft.Extensions.AI;
using Spectre.Console;

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
        var nyxIdCredentialStore = new NyxIdCredentialStore();
        var nyxIdSettings = NyxIdCliSettingsLoader.Load(nyxIdCredentialStore);
        var nyxIdAuthService = new NyxIdAuthService(credentialStore: nyxIdCredentialStore);
        var nyxIdTokenProvider = new NyxIdTokenProvider(nyxIdCredentialStore, nyxIdAuthService);

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

        var skillLoader = new SkillLoader();
        var contextProvider = new ContextProvider
        {
            WorkingDirectory = workingDirectory,
            SkillLoader = skillLoader,
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
        if (options.UseNyxId && aiProvider == AiProvider.Ollama)
        {
            Console.Error.WriteLine("--nyxid 只支持 anthropic 和 openai provider。");
            return 1;
        }

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
        var nyxIdRouting = options.UseNyxId
            ? new NyxIdRoutingContext(
                nyxIdSettings.ActiveBaseUrl,
                nyxIdTokenProvider,
                nyxIdSettings.HasStoredCredentials)
            : null;
        using var chatClientRuntime = ChatClientRuntime.Create(
            aiProvider,
            model,
            config,
            anthropicSettings,
            nyxIdRouting);
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
        var agentWorkspaceManager = new GitWorktreeAgentWorkspaceManager();
        var agentTaskRuntime = await PersistentAgentTaskRuntime.CreateAsync(
            journal,
            metadataEntries,
            autoPrunePolicy: agentSettings.Settings.BuildRetentionPolicy());
        var agentWorktreeRuntime = await PersistentAgentManagedWorktreeRuntime.CreateAsync(
            journal,
            metadataEntries,
            agentWorkspaceManager);
        var agentTeamRuntime = await PersistentAgentTeamRuntime.CreateAsync(
            journal,
            metadataEntries);
        var agentMessageRuntime = await PersistentAgentMessageRuntime.CreateAsync(
            journal,
            metadataEntries);
        var remoteTriggerRuntime = await PersistentAgentRemoteTriggerRuntime.CreateAsync(
            journal,
            agentTaskRuntime,
            metadataEntries);
        var channelConnectionManager = new ChannelConnectionManager();
        var channelRouter = new ChannelRouter(
            new BridgeChannelTransport(channelConnectionManager, agentMessageRuntime),
            new UdsChannelTransport(channelConnectionManager, agentMessageRuntime));
        var messageActivationRuntime = new InMemoryAgentMessageActivationRuntime();
        var toolRegistry = BuildToolRegistry(
            skillLoader,
            workingDirectory,
            providerRouter,
            managedPolicy.AllowWebSearch,
            () => config.Model,
            () => config.Provider,
            chatClient,
            hooks,
            agentWorkspaceManager,
            agentTaskRuntime,
            agentWorktreeRuntime,
            agentTeamRuntime,
            agentMessageRuntime,
            remoteTriggerRuntime,
            todoRuntime,
            cronRuntime,
            channelRouter,
            messageActivationRuntime,
            agentRuntimeOptions,
            agentSettings.Settings.BackgroundRunConcurrency);
        var commandRegistry = BuildCommandRegistry(
            skillLoader.Load(workingDirectory),
            nyxIdAuthService,
            nyxIdCredentialStore,
            nyxIdSettings.ActiveBaseUrl,
            transcriptStore,
            memoryLayout,
            managedPolicy.AnthropicSettings,
            managedSettings,
            new NyxIdRuntimeConfig(
                nyxIdSettings.DefaultBaseUrl,
                nyxIdSettings.ActiveBaseUrl,
                nyxIdSettings.HasStoredCredentials,
                nyxIdSettings.BaseUrlFromEnvironment),
            typeof(Program).Assembly);
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
        await using var remoteTriggerScheduler = new AgentRemoteTriggerScheduler(remoteTriggerRuntime);
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
        var lineEditorHistoryPath = CreateHistoryPath();
        var lineEditorHistory = LineEditorHistoryStore.Load(lineEditorHistoryPath);

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
            lineEditorHistory,
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

        var exitCode = 0;
        try
        {
            exitCode = nonInteractiveOptions != null
                ? await shell.RunNonInteractiveAsync(nonInteractiveOptions)
                : await shell.RunAsync(options.InitialPrompt);
            await PublishAppStateAsync();
            return exitCode;
        }
        finally
        {
            try
            {
                await LineEditorHistoryStore.SaveAsync(lineEditorHistoryPath, lineEditorHistory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Persisting interactive history is best effort and should not fail the CLI.
            }
        }
    }

    private static ToolRegistry BuildToolRegistry(
        SkillLoader skillLoader,
        string workingDirectory,
        IProviderCapabilityRouter providerRouter,
        bool allowWebSearch,
        Func<string?> currentModelAccessor,
        Func<AiProvider> currentProviderAccessor,
        IChatClient chatClient,
        IHookRuntime hooks,
        IAgentWorkspaceManager workspaceManager,
        IAgentTaskRuntime agentTaskRuntime,
        IAgentManagedWorktreeRuntime agentWorktreeRuntime,
        IAgentTeamRuntime agentTeamRuntime,
        IAgentMessageRuntime agentMessageRuntime,
        IAgentRemoteTriggerRuntime remoteTriggerRuntime,
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
        registry.Register(new SkillTool(skillLoader, workingDirectory));
        registry.Register(new BashTool());
        registry.Register(new FileReadTool());
        registry.Register(new FileWriteTool());
        registry.Register(new FileEditTool());
        registry.Register(new NotebookEditTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new AskUserQuestionTool());
        registry.Register(new TodoWriteTool(todoRuntime));
        registry.Register(new WebFetchTool());
        registry.Register(new AgentTool(
            new QueryEngineAgentRunner(chatClient, hooks: hooks, workspaceManager: workspaceManager),
            providerRouter,
            agentTaskRuntime,
            agentTeamRuntime,
            agentMessageRuntime,
            hooks,
            backgroundRunScheduler,
            messageActivationRuntime,
            agentWorktreeRuntime,
            runtimeOptions: agentRuntimeOptions,
            skillLoader: skillLoader));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "EnterWorktree",
            () => new EnterWorktreeTool(agentWorktreeRuntime),
            Aliases: ["EnterWorktreeTool"],
            Keywords: ["worktree", "workspace", "git", "isolation", "enter"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "ExitWorktree",
            () => new ExitWorktreeTool(agentWorktreeRuntime),
            Aliases: ["ExitWorktreeTool"],
            Keywords: ["worktree", "workspace", "git", "cleanup", "exit"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TaskCreate",
            () => new TaskCreateTool(agentTaskRuntime),
            Aliases: ["TaskCreateTool"],
            Keywords: ["task", "background", "create", "job"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TaskGet",
            () => new TaskGetTool(agentTaskRuntime),
            Aliases: ["TaskGetTool"],
            Keywords: ["task", "background", "status", "details"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TaskUpdate",
            () => new TaskUpdateTool(agentTaskRuntime),
            Aliases: ["TaskUpdateTool"],
            Keywords: ["task", "background", "update", "status"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TaskList",
            () => new TaskListTool(agentTaskRuntime),
            Aliases: ["TaskListTool"],
            Keywords: ["task", "background", "list", "queue"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TaskStop",
            () => new TaskStopTool(agentTaskRuntime),
            Aliases: ["TaskStopTool"],
            Keywords: ["task", "background", "stop", "cancel"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "TaskOutput",
            () => new TaskOutputTool(agentTaskRuntime),
            Aliases: ["TaskOutputTool"],
            Keywords: ["task", "background", "output", "logs"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "RemoteTrigger",
            () => new RemoteTriggerTool(remoteTriggerRuntime),
            Aliases: ["RemoteTriggerTool"],
            Keywords: ["trigger", "remote", "webhook", "schedule", "cron"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "Monitor",
            () => new MonitorTool(agentTaskRuntime),
            Aliases: ["MonitorTool"],
            Keywords: ["monitor", "tail", "logs", "stdout", "stderr"]));
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
            "Sleep",
            () => new SleepTool(),
            Aliases: ["SleepTool"],
            Keywords: ["sleep", "wait", "delay", "pause", "timer"]));
        registry.RegisterDeferred(new DeferredToolRegistration(
            "ScheduleWakeup",
            () => new ScheduleWakeupTool(cronRuntime),
            Aliases: ["ScheduleWakeupTool"],
            Keywords: ["sleep", "wait", "wakeup", "wake", "schedule", "timer"]));
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

    private static string CreateHistoryPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aexon", "history.txt");
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

    private static CommandRegistry BuildCommandRegistry(
        IReadOnlyDictionary<string, Skill> skills,
        NyxIdAuthService nyxIdAuthService,
        NyxIdCredentialStore nyxIdCredentialStore,
        string nyxIdBaseUrl,
        ITranscriptStore transcriptStore,
        MemdirLayout memdirLayout,
        AnthropicClientSettings anthropicSettings,
        ManagedSettingsLoadResult managedSettings,
        NyxIdRuntimeConfig nyxIdRuntimeConfig,
        Assembly productAssembly)
    {
        var registry = new CommandRegistry();
        registry.Register(new AgentsCommand());
        registry.Register(new AwayCommand());
        registry.Register(new BranchCommand());
        registry.Register(new ClearCommand());
        registry.Register(new CommitCommand());
        registry.Register(new CompactCommand());
        registry.Register(new ConfigCommand(anthropicSettings, managedSettings, nyxIdRuntimeConfig));
        registry.Register(new CostCommand());
        registry.Register(new DiffCommand());
        registry.Register(new DoctorCommand(anthropicSettings, nyxIdRuntimeConfig));
        registry.Register(new EffortCommand());
        registry.Register(new ExitCommand());
        registry.Register(new FastCommand());
        registry.Register(new HelpCommand());
        registry.Register(new InitCommand());
        registry.Register(new LoginCommand(nyxIdAuthService, nyxIdCredentialStore, nyxIdBaseUrl));
        registry.Register(new LogoutCommand(nyxIdAuthService, nyxIdCredentialStore));
        registry.Register(new MailboxCommand());
        registry.Register(new MemoryCommand(memdirLayout));
        registry.Register(new MicrocompactCommand());
        registry.Register(new ModeCommand());
        registry.Register(new ModelCommand());
        registry.Register(new PartialCompactCommand());
        registry.Register(new PermissionsCommand());
        registry.Register(new PlanCommand());
        registry.Register(new PrCommand());
        registry.Register(new RenameCommand());
        registry.Register(new ResumeCommand(transcriptStore));
        registry.Register(new ReviewCommand());
        registry.Register(new SessionCommand());
        registry.Register(new SessionMemoryCompactCommand());
        registry.Register(new StatsCommand(transcriptStore));
        registry.Register(new StatusCommand());
        registry.Register(new TagCommand());
        registry.Register(new TeamCommand());
        registry.Register(new TitleCommand());
        registry.Register(new VersionCommand(productAssembly));

        foreach (var skill in skills.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (registry.Get(skill.Name) != null)
                continue;

            registry.Register(new SkillCommand(skill));
        }

        return registry;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Usage:
  aexon [--cwd <path>] [--model <model>] [--provider <name>] [--nyxid] [--resume <session>] [--continue] [--fork-session] [--settings <path>] [prompt]
  aexon --print [-p] [--output-format <text|markdown|json>] [--approval-mode <allow|deny>] [--max-turns <n>] [prompt]

Options:
  --cwd <path>       Working directory for this session
  --model <model>    Main model or alias (sonnet / opus / haiku / gpt-4o / o3 / ...)
  --provider <name>  AI provider: anthropic (default), openai, or ollama
  --nyxid            Route Anthropic/OpenAI requests through the NyxID proxy
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
  NYXID_BASE_URL     NyxID base URL for /login defaults and --nyxid routing
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
        private readonly List<string> _inputHistory;
        private readonly AgentRuntimeOptions _agentRuntimeOptions;
        private readonly string? _startupNote;
        private readonly Func<Task>? _afterInputAsync;
        private readonly ToolProgressRenderer _toolProgressRenderer = new();
        private readonly PermissionPrompt _permissionPrompt = new();
        private readonly StatusBar _statusBar = new();
        private readonly DateTimeOffset _sessionStartedAt = DateTimeOffset.UtcNow;
        private int _sessionTurnCount;
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
            List<string> inputHistory,
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
            _inputHistory = inputHistory;
            _startupNote = startupNote;
            _afterInputAsync = afterInputAsync;
        }

        public async Task<int> RunAsync(string? initialPrompt)
        {
            var interactive = !Console.IsInputRedirected && string.IsNullOrWhiteSpace(initialPrompt);
            var lineEditor = interactive
                ? new LineEditor(
                    _commandRegistry,
                    _inputHistory,
                    _workingDirectory)
                : null;
            if (interactive)
                PrintBanner();

            var commandContext = new CommandContext
            {
                WriteLine = Console.WriteLine,
                Tools = _toolRegistry,
                QueryEngine = _queryEngine,
                SubmitPromptAsync = RunQueryAsync,
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
                ReadInputLine = Console.ReadLine,
                SessionStartedAt = _sessionStartedAt,
                SessionTurnCountProvider = () => _sessionTurnCount,
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
                string? input;
                if (interactive)
                {
                    Console.WriteLine();
                    input = await lineEditor!.ReadLineAsync();
                }
                else
                {
                    input = Console.ReadLine();
                }

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
                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        Console.Write(result.Output);
                        if (!result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                            Console.WriteLine();
                    }
                    break;

                case NonInteractiveOutputFormat.Text:
                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        var markdownWriter = CreateMarkdownWriter(enableRendering: !Console.IsOutputRedirected);
                        if (markdownWriter.Enabled)
                        {
                            markdownWriter.WriteComplete(result.Output);
                        }
                        else
                        {
                            Console.Write(result.Output);
                            if (!result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                                Console.WriteLine();
                        }
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
            var wroteAssistantTextInCurrentTurn = false;
            var markdownWriter = CreateMarkdownWriter(enableRendering: !Console.IsOutputRedirected);

            await foreach (var evt in _queryEngine.SubmitMessageAsync(input))
            {
                switch (evt)
                {
                    case TextDeltaEvent text:
                        wroteAssistantTextInCurrentTurn = true;
                        markdownWriter.Write(text.Text);
                        break;

                    case ThinkingDeltaEvent:
                        // Hide raw thinking text for now to keep the terminal output clean.
                        break;

                    case ToolUseStartEvent toolUse:
                        if (wroteAssistantTextInCurrentTurn)
                        {
                            markdownWriter.Flush();
                            Console.WriteLine();
                        }

                        _toolProgressRenderer.Start(
                            toolUse.ToolUseId,
                            toolUse.ToolName,
                            toolUse.Input);
                        break;

                    case ToolProgressEvent toolProgress:
                        _toolProgressRenderer.ReportProgress(
                            toolProgress.ToolUseId,
                            toolProgress.Message);
                        break;

                    case PermissionRequestEvent permissionRequest:
                        permissionRequest.SetResponse(AskForPermission(permissionRequest));
                        break;

                    case ToolResultEvent toolResult:
                        _toolProgressRenderer.Complete(
                            toolResult.ToolUseId,
                            toolResult.ToolName,
                            toolResult.IsError);
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
                        markdownWriter.Flush();
                        if (wroteAssistantTextInCurrentTurn)
                            Console.WriteLine();
                        _statusBar.Refresh(new StatusBarSnapshot(
                            _queryEngine.CurrentModel,
                            _queryEngine.TotalUsage,
                            DateTimeOffset.UtcNow - _sessionStartedAt));
                        wroteAssistantTextInCurrentTurn = false;
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

                    case QueryCompleteEvent complete:
                        _sessionTurnCount += complete.TurnCount;
                        if (!complete.Success)
                        {
                            markdownWriter.Flush();
                            Console.WriteLine();
                            Console.WriteLine($"请求失败: {complete.ErrorMessage}");
                        }

                        break;
                }
            }
        }

        private static SpectreMarkdownConsoleWriter CreateMarkdownWriter(bool enableRendering) =>
            new(
                AnsiConsole.Console,
                Console.Write,
                enableRendering);

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

            var decision = _permissionPrompt.Prompt(
                request.ToolName,
                request.Description,
                request.Input);

            if (decision == PermissionPromptDecision.AlwaysAllow)
            {
                AddAlwaysAllowRule(request);
                return true;
            }

            return decision == PermissionPromptDecision.Yes;
        }

        private void AddAlwaysAllowRule(PermissionRequestEvent request)
        {
            var toolName = request.ToolName.Trim();
            var ruleContent = PermissionPrompt.ExtractRuleTarget(request.Input);
            var rule = PermissionRule.Create(
                PermissionBehavior.Allow,
                toolName,
                ruleContent);

            if (!_permissionContext.Rules.Any(existing =>
                    existing.Behavior == PermissionBehavior.Allow &&
                    string.Equals(existing.ToolName, rule.ToolName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.RuleContent, rule.RuleContent, StringComparison.OrdinalIgnoreCase)))
            {
                _permissionContext.Rules.Add(rule);
            }

            Console.WriteLine($"已加入本次会话的允许规则: {rule.ToExpression()}");
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
