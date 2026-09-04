using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aexon.Core.Compaction;
using Aexon.Core.Hooks;
using Aexon.Core.Memory;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Storage;
using Aexon.Core.Tools;
using Microsoft.Extensions.AI;
using ApiCacheControlEphemeral = Anthropic.Models.Messages.CacheControlEphemeral;
using ApiContentBlockParam = Anthropic.Models.Messages.ContentBlockParam;
using ApiInputSchema = Anthropic.Models.Messages.InputSchema;
using ApiMessageCreateParams = Anthropic.Models.Messages.MessageCreateParams;
using ApiMessageCreateParamsSystem = Anthropic.Models.Messages.MessageCreateParamsSystem;
using ApiMessageParam = Anthropic.Models.Messages.MessageParam;
using ApiMessageParamContent = Anthropic.Models.Messages.MessageParamContent;
using ApiRawContentBlockDeltaEvent = Anthropic.Models.Messages.RawContentBlockDeltaEvent;
using ApiRawContentBlockStartEvent = Anthropic.Models.Messages.RawContentBlockStartEvent;
using ApiRawContentBlockStopEvent = Anthropic.Models.Messages.RawContentBlockStopEvent;
using ApiRawMessageStreamEvent = Anthropic.Models.Messages.RawMessageStreamEvent;
using ApiRole = Anthropic.Models.Messages.Role;
using ApiTextBlockParam = Anthropic.Models.Messages.TextBlockParam;
using ApiThinkingBlockParam = Anthropic.Models.Messages.ThinkingBlockParam;
using ApiTool = Anthropic.Models.Messages.Tool;
using ApiToolChoiceAuto = Anthropic.Models.Messages.ToolChoiceAuto;
using ApiToolResultBlockParam = Anthropic.Models.Messages.ToolResultBlockParam;
using ApiToolUnion = Anthropic.Models.Messages.ToolUnion;
using ApiToolUseBlockParam = Anthropic.Models.Messages.ToolUseBlockParam;

namespace Aexon.Core.Query;

/// <summary>
/// Runs the main conversation loop, including model calls, tool execution, and compaction.
/// </summary>
public class QueryEngine : IAsyncDisposable, IPlanModeController, IAwayModeController
{
    private const int MaxPromptCacheBreakpoints = 4;
    private const int PromptCacheLookbackWindowBlocks = 20;
    private readonly IChatClient _chatClient;
    private readonly ToolRegistry _tools;
    private readonly IToolRuntime _toolRuntime;
    private readonly IConversationCompactor _compactor;
    private readonly IMicroCompactor _microCompactor;
    private readonly ISessionMemoryCompactor _sessionMemoryCompactor;
    private readonly IContextPressurePipeline _contextPressurePipeline;
    private readonly IHookRuntime _hooks;
    private readonly QueryEngineConfig _config;
    private readonly Context.ContextProvider _contextProvider;
    private readonly IConversationJournal? _journal;
    private readonly SessionMemoryFile? _sessionMemoryFile;
    private readonly AskUserQuestionHandler? _askUserQuestion;
    private readonly ConversationSessionMetadata _sessionMetadata;
    private readonly AttachmentRegistry _attachmentRegistry = new();
    private readonly ConcurrentQueue<ConversationMessage> _pendingExternalMessages = new();
    private readonly List<ConversationMessage> _messages;
    private TokenUsage _totalUsage;
    private int _consecutiveAutoCompactFailures;
    private bool _sessionStarted;
    private bool _sessionEnded;
    private bool _promptCacheWarm;
    private PermissionMode _planModeResumeMode;
    private bool _awayModeActive;
    private DateTimeOffset? _awayEnteredAt;
    private string? _awayTriggerReason;

    public QueryEngine(
        IChatClient chatClient,
        ToolRegistry tools,
        IPermissionChecker permissions,
        QueryEngineConfig config,
        Context.ContextProvider contextProvider,
        IToolRuntime? toolRuntime = null,
        IConversationCompactor? compactor = null,
        IMicroCompactor? microCompactor = null,
        ISessionMemoryCompactor? sessionMemoryCompactor = null,
        IContextPressurePipeline? contextPressurePipeline = null,
        IHookRuntime? hooks = null,
        IConversationJournal? journal = null,
        SessionMemoryFile? sessionMemoryFile = null,
        IReadOnlyList<ConversationMessage>? initialMessages = null,
        TokenUsage? initialUsage = null,
        ConversationSessionMetadata? initialMetadata = null,
        AskUserQuestionHandler? askUserQuestion = null)
    {
        _chatClient = chatClient;
        _tools = tools;
        var effectiveHooks = hooks ?? HookRuntime.Empty;
        _toolRuntime = toolRuntime ?? new StreamingToolExecutor(tools, permissions, effectiveHooks);
        _compactor = compactor ?? new HeuristicConversationCompactor();
        _microCompactor = microCompactor ?? new TimeBasedMicroCompactor();
        _sessionMemoryCompactor = sessionMemoryCompactor ?? new SessionMemoryCompactor();
        _contextPressurePipeline = contextPressurePipeline ??
                                  new DefaultContextPressurePipeline(
                                      microCompactor: _microCompactor,
                                      sessionMemoryCompactor: _sessionMemoryCompactor,
                                      conversationCompactor: _compactor);
        _hooks = effectiveHooks;
        _config = config;
        _contextProvider = contextProvider;
        _journal = journal;
        _sessionMemoryFile = sessionMemoryFile;
        _askUserQuestion = askUserQuestion;
        _sessionMetadata = initialMetadata?.Clone() ?? journal?.Metadata ?? new ConversationSessionMetadata();
        if (_sessionMetadata.Mode is PermissionMode sessionMode)
            _contextProvider.PermissionContext.Mode = sessionMode;
        if (_sessionMetadata.Effort is QueryEffortLevel sessionEffort)
            _config.Effort = sessionEffort;
        _messages = initialMessages?.ToList() ?? [];
        _totalUsage = initialUsage ?? ComputeTotalUsage(_messages);
        foreach (var attachment in _sessionMetadata.Attachments.Values)
            _attachmentRegistry.Register(attachment);
        _planModeResumeMode = ResolveInitialPlanModeResumeMode();
        _awayModeActive = _sessionMetadata.AwayEnteredAt.HasValue;
        _awayEnteredAt = _sessionMetadata.AwayEnteredAt;
        _awayTriggerReason = _sessionMetadata.AwayTriggerReason;
    }

    /// <summary>
    /// Gets the current conversation messages.
    /// </summary>
    public IReadOnlyList<ConversationMessage> Messages => _messages;

    /// <summary>
    /// Gets the accumulated token usage.
    /// </summary>
    public TokenUsage TotalUsage => _totalUsage;

    /// <summary>
    /// Gets the active model identifier.
    /// </summary>
    public string CurrentModel => _config.Model;

    /// <summary>
    /// Gets the active effort profile.
    /// </summary>
    public QueryEffortLevel CurrentEffort => _config.Effort;

    /// <summary>
    /// Resolves a model alias and updates the active model.
    /// </summary>
    public string SetModel(string modelOrAlias)
    {
        _config.Model = ClaudeModels.Resolve(modelOrAlias);
        return _config.Model;
    }

    public async Task<string> SetModelAsync(
        string modelOrAlias,
        CancellationToken ct = default)
    {
        var resolved = SetModel(modelOrAlias);
        await PersistRuntimeStateAsync(ct);
        return resolved;
    }

    public async Task SetEffortAsync(
        QueryEffortLevel effort,
        CancellationToken ct = default)
    {
        _config.Effort = effort;
        _sessionMetadata.Effort = effort;
        if (_journal != null)
        {
            await _journal.UpdateMetadataAsync(
                metadata => metadata.Effort = effort,
                ct);
        }
    }

    public string? SessionId => _journal?.SessionId;

    public string? TranscriptPath => _journal?.TranscriptPath;

    public ConversationSessionMetadata SessionMetadata => _sessionMetadata.Clone();
    public bool IsPlanModeActive => CurrentPermissionMode == PermissionMode.Plan;

    public PermissionMode PlanModeResumeMode =>
        _planModeResumeMode == PermissionMode.Plan
            ? PermissionMode.Default
            : _planModeResumeMode;

    public ApiQuotaStatus? LatestQuotaStatus { get; private set; }

    /// <summary>
    /// Sends a user message through the agent loop and streams query events.
    /// </summary>
    public async IAsyncEnumerable<QueryEvent> SubmitMessageAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var turnCount = 0;
        string? lastStopReason = null;

        await EnsureSessionStartedAsync(ct);
        FlushPendingExternalMessages();

        await AddMessageAsync(UserMessage.FromText(userInput), ct);

        while (!ct.IsCancellationRequested)
        {
            turnCount++;

            var compactionEvents = await PrepareContextForTurnAsync(ct);
            foreach (var compactionEvent in compactionEvents)
                yield return compactionEvent;

            var systemPrompt = await BuildSystemPromptAsync();
            var promptCachingEnabled = SupportsPromptCaching(_config.Model);
            var hasSystemPromptCacheBreakpoint =
                promptCachingEnabled && !string.IsNullOrWhiteSpace(systemPrompt);
            var messagePromptCacheBreakpointBudget = promptCachingEnabled
                ? MaxPromptCacheBreakpoints - (hasSystemPromptCacheBreakpoint ? 1 : 0)
                : 0;
            var chatMessages = ChatMessageConverter.ToMeaiMessages(_messages);
            var toolDefs = await GetAvailableToolDefinitionsAsync();

            yield return new StatusEvent("calling_api");

            var assistantTurn = new AssistantTurnAccumulator();
            var chatOptions = BuildChatOptions(
                systemPrompt,
                toolDefs,
                promptCachingEnabled,
                hasSystemPromptCacheBreakpoint,
                messagePromptCacheBreakpointBudget);

            string? requestError = null;

            if (_config.UseStreamingApi)
            {
                await using var streamEnumerator =
                    StreamAssistantTurnAsync(chatMessages, chatOptions, assistantTurn, ct).GetAsyncEnumerator(ct);

                while (true)
                {
                    QueryEvent? nextEvent;
                    try
                    {
                        if (!await streamEnumerator.MoveNextAsync())
                            break;

                        nextEvent = streamEnumerator.Current;
                    }
                    catch (Exception ex)
                    {
                        UpdateQuotaStatus(TryGetQuotaStatus(ex));
                        requestError = ex.Message;
                        break;
                    }

                    yield return nextEvent;
                }
            }
            else
            {
                try
                {
                    await CollectAssistantTurnAsync(chatMessages, chatOptions, assistantTurn, ct);
                }
                catch (Exception ex)
                {
                    UpdateQuotaStatus(TryGetQuotaStatus(ex));
                    requestError = ex.Message;
                }
            }

            if (requestError != null)
            {
                await _hooks.OnStopFailureAsync(
                    BuildStopHookContext(
                        success: false,
                        errorMessage: requestError,
                        duration: DateTimeOffset.UtcNow - startTime,
                        turnCount),
                    ct);

                await AddRuntimeMessagesAsync(
                    CreateStopLifecycleMessages(
                        success: false,
                        duration: DateTimeOffset.UtcNow - startTime,
                        turnCount: turnCount,
                        stopReason: lastStopReason,
                        errorMessage: requestError),
                    ct);

                yield return new QueryCompleteEvent
                {
                    Success = false,
                    Duration = DateTimeOffset.UtcNow - startTime,
                    TurnCount = turnCount,
                    TotalUsage = _totalUsage,
                    ErrorMessage = requestError,
                };
                yield break;
            }

            PromptCacheStatusEvent? promptCacheStatusEvent = null;
            if (assistantTurn.Usage != null)
            {
                _totalUsage += assistantTurn.Usage;
                promptCacheStatusEvent = CreatePromptCacheStatusEvent(
                    promptCachingEnabled,
                    assistantTurn.Usage);
            }

            lastStopReason = assistantTurn.StopReason;

            if (!_config.UseStreamingApi)
            {
                foreach (var evt in EmitBufferedAssistantTurnEvents(assistantTurn))
                    yield return evt;
            }

            if (assistantTurn.MessageId != null || assistantTurn.Usage != null || assistantTurn.ContentBlocks.Count > 0)
                yield return new MessageEndEvent(assistantTurn.StopReason, assistantTurn.Usage);

            if (promptCacheStatusEvent != null)
                yield return promptCacheStatusEvent;

            await AddMessageAsync(new AssistantMessage
            {
                Content = assistantTurn.ContentBlocks,
                StopReason = assistantTurn.StopReason,
                Usage = assistantTurn.Usage,
            }, ct);

            if (assistantTurn.ToolUseBlocks.Count == 0)
                break;

            if (turnCount >= _config.MaxTurns)
            {
                yield return new TextDeltaEvent(
                    $"\n\n[Reached maximum turn limit of {_config.MaxTurns}]");
                break;
            }

            await foreach (var evt in ExecuteToolCallsAsync(assistantTurn.ToolUseBlocks, ct))
            {
                yield return evt;
            }
        }

        await _hooks.OnStopAsync(
            BuildStopHookContext(
                success: true,
                errorMessage: null,
                duration: DateTimeOffset.UtcNow - startTime,
                turnCount),
            ct);

        await AddRuntimeMessagesAsync(
            CreateStopLifecycleMessages(
                success: true,
                duration: DateTimeOffset.UtcNow - startTime,
                turnCount: turnCount,
                stopReason: lastStopReason,
                errorMessage: null),
            ct);

        yield return new QueryCompleteEvent
        {
            Success = true,
            Duration = DateTimeOffset.UtcNow - startTime,
            TurnCount = turnCount,
            TotalUsage = _totalUsage,
        };
    }

    /// <summary>
    /// Executes the tool calls returned by the model.
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> ExecuteToolCallsAsync(
        List<ToolUseBlock> toolUseBlocks,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var context = CreateToolContext(ct);
        await foreach (var update in _toolRuntime.RunBatchAsync(toolUseBlocks, context, ct))
        {
            switch (update)
            {
                case ToolPermissionRequestUpdate request:
                    {
                        await AddMessageAsync(
                            CreatePermissionRetryMessage(request),
                            ct);

                        var permissionEvent = new PermissionRequestEvent
                        {
                            ToolName = request.Invocation.Name,
                            Description = request.Description,
                            Input = request.ObservedInput,
                        };

                        yield return permissionEvent;
                        var approved = await permissionEvent.WaitForResponseAsync();
                        request.SetResponse(approved);
                        break;
                    }
                case ToolProgressUpdate progress:
                    {
                        yield return new ToolProgressEvent(
                            progress.ToolUseId,
                            progress.Progress.Message ?? progress.Progress.Type);
                        break;
                    }
                case ToolCompletedUpdate completed:
                    {
                        yield return new ToolResultEvent(
                            completed.Outcome.Invocation.ToolUseId,
                            completed.Outcome.Invocation.Name,
                            completed.Outcome.Result.Data,
                            completed.Outcome.Result.IsError);

                        if (completed.Outcome.Result.NewMessages is { Count: > 0 } newMessages)
                            await AddRuntimeMessagesAsync(newMessages, ct);

                        await AddMessageAsync(UserMessage.FromToolResult(
                            completed.Outcome.Invocation.ToolUseId,
                            completed.Outcome.Result.Data,
                            completed.Outcome.Result.IsError), ct);

                        await AddMessageAsync(
                            CreateToolUseSummaryMessage(completed.Outcome),
                            ct);
                        break;
                    }
            }
        }
    }

    private ToolExecutionContext CreateToolContext(CancellationToken cancellationToken) => new()
    {
        WorkingDirectory = _contextProvider.WorkingDirectory,
        PermissionContext = _contextProvider.GetPermissionContext(),
        Tools = GetAvailableTools(),
        Messages = _messages,
        CancellationToken = cancellationToken,
        IsNonInteractiveSession = _askUserQuestion == null,
        AskUserQuestionAsync = _askUserQuestion,
        MainLoopModel = _config.Model,
        MainLoopProvider = _config.Provider,
        LoopSessionId = SessionId,
    };

    private async Task<string> BuildSystemPromptAsync()
    {
        return await _contextProvider.BuildSystemPromptAsync(
            GetAvailableTools(),
            _config);
    }

    /// <summary>
    /// Converts internal messages into Anthropic Messages API payloads.
    /// </summary>
    private static List<ApiMessageParam> ConvertToApiMessages(
        List<ConversationMessage> messages,
        int promptCacheMessageBreakpointBudget)
    {
        var pendingMessages = new List<PendingApiMessage>();
        var promptCacheCandidates = new List<PromptCacheBlockCandidate>();
        var stableMessageCount = promptCacheMessageBreakpointBudget > 0
            ? GetStablePromptCacheMessageCount(messages)
            : 0;
        var contentPosition = 0;

        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var msg = messages[messageIndex];
            var includeInStablePrefix = messageIndex < stableMessageCount;

            switch (msg)
            {
                case UserMessage userMsg:
                    var userContents = new List<ApiContentBlockParam>();
                    foreach (var block in userMsg.Content)
                    {
                        if (block is TextBlock tb)
                        {
                            var contentIndex = userContents.Count;
                            userContents.Add(new ApiContentBlockParam(new ApiTextBlockParam(tb.Text)));
                            Action? applyCacheControl = null;
                            if (!string.IsNullOrWhiteSpace(tb.Text))
                            {
                                applyCacheControl = () => userContents[contentIndex] =
                                    new ApiContentBlockParam(new ApiTextBlockParam(tb.Text)
                                    {
                                        CacheControl = CreatePromptCacheControl(),
                                    });
                            }

                            RegisterPromptCacheCandidate(
                                promptCacheCandidates,
                                includeInStablePrefix,
                                ref contentPosition,
                                applyCacheControl);
                        }
                        else if (block is ToolResultBlock trb)
                        {
                            var contentIndex = userContents.Count;
                            userContents.Add(new ApiContentBlockParam(new ApiToolResultBlockParam(trb.ToolUseId)
                            {
                                Content = trb.Content,
                                IsError = trb.IsError,
                            }));
                            RegisterPromptCacheCandidate(
                                promptCacheCandidates,
                                includeInStablePrefix,
                                ref contentPosition,
                                () => userContents[contentIndex] =
                                    new ApiContentBlockParam(new ApiToolResultBlockParam(trb.ToolUseId)
                                    {
                                        Content = trb.Content,
                                        IsError = trb.IsError,
                                        CacheControl = CreatePromptCacheControl(),
                                    }));
                        }
                    }

                    pendingMessages.Add(new PendingApiMessage(ApiRole.User, userContents));
                    break;

                case AssistantMessage assistantMsg:
                    var assistantContents = new List<ApiContentBlockParam>();
                    foreach (var block in assistantMsg.Content)
                    {
                        if (block is TextBlock tb)
                        {
                            var contentIndex = assistantContents.Count;
                            assistantContents.Add(new ApiContentBlockParam(new ApiTextBlockParam(tb.Text)));
                            Action? applyCacheControl = null;
                            if (!string.IsNullOrWhiteSpace(tb.Text))
                            {
                                applyCacheControl = () => assistantContents[contentIndex] =
                                    new ApiContentBlockParam(new ApiTextBlockParam(tb.Text)
                                    {
                                        CacheControl = CreatePromptCacheControl(),
                                    });
                            }

                            RegisterPromptCacheCandidate(
                                promptCacheCandidates,
                                includeInStablePrefix,
                                ref contentPosition,
                                applyCacheControl);
                        }
                        else if (block is ToolUseBlock tub)
                        {
                            var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                                tub.Input.GetRawText()) ?? new Dictionary<string, JsonElement>();
                            var contentIndex = assistantContents.Count;
                            assistantContents.Add(new ApiContentBlockParam(new ApiToolUseBlockParam
                            {
                                ID = tub.ToolUseId,
                                Name = tub.Name,
                                Input = input,
                            }));
                            RegisterPromptCacheCandidate(
                                promptCacheCandidates,
                                includeInStablePrefix,
                                ref contentPosition,
                                () => assistantContents[contentIndex] =
                                    new ApiContentBlockParam(new ApiToolUseBlockParam
                                    {
                                        ID = tub.ToolUseId,
                                        Name = tub.Name,
                                        Input = input,
                                        CacheControl = CreatePromptCacheControl(),
                                    }));
                        }
                        else if (block is ThinkingBlock thinking && !string.IsNullOrWhiteSpace(thinking.Signature))
                        {
                            assistantContents.Add(new ApiContentBlockParam(new ApiThinkingBlockParam
                            {
                                Signature = thinking.Signature,
                                Thinking = thinking.Text,
                            }));
                            RegisterPromptCacheCandidate(
                                promptCacheCandidates,
                                includeInStablePrefix,
                                ref contentPosition,
                                null);
                        }
                    }

                    if (assistantContents.Count == 0)
                        continue;

                    pendingMessages.Add(new PendingApiMessage(ApiRole.Assistant, assistantContents));
                    break;
            }
        }

        ApplyPromptCacheBreakpoints(promptCacheCandidates, promptCacheMessageBreakpointBudget);
        return pendingMessages
            .Select(message => new ApiMessageParam
            {
                Role = message.Role,
                Content = new ApiMessageParamContent(message.Content),
            })
            .ToList();
    }

    private static ApiMessageCreateParamsSystem CreateApiSystemPrompt(
        string systemPrompt,
        bool includeCacheBreakpoint)
    {
        if (!includeCacheBreakpoint)
            return new ApiMessageCreateParamsSystem(systemPrompt, null);

        var textBlock = new ApiTextBlockParam(systemPrompt)
        {
            CacheControl = CreatePromptCacheControl(),
        };

        return new ApiMessageCreateParamsSystem(new[] { textBlock }, null);
    }

    private static string? ToStopReason(ChatFinishReason? finishReason) =>
        NormalizeStopReason(finishReason?.Value);

    private static string? NormalizeStopReason(string? stopReason) =>
        stopReason switch
        {
            null => null,
            "tool_calls" => "tool_use",
            "stop" => "end_turn",
            "length" => "max_tokens",
            "content_filter" => "refusal",
            var value => value,
        };

    private static List<ApiToolUnion> ConvertToolDefinitions(
        IReadOnlyList<JsonElement> defs)
    {
        var tools = new List<ApiToolUnion>();

        foreach (var def in defs)
        {
            var schema = JsonSerializer.Deserialize<ApiInputSchema>(
                def.GetProperty("input_schema").GetRawText());

            if (schema == null)
                continue;

            var tool = new ApiTool
            {
                Name = def.GetProperty("name").GetString()!,
                Description = def.GetProperty("description").GetString(),
                InputSchema = schema,
                Strict = false,
            };

            tools.Add(new ApiToolUnion(tool));
        }

        return tools;
    }

    private ChatOptions BuildChatOptions(
        string systemPrompt,
        IReadOnlyList<JsonElement> toolDefs,
        bool promptCachingEnabled,
        bool hasSystemPromptCacheBreakpoint,
        int messagePromptCacheBreakpointBudget)
    {
        var profile = QueryExecutionProfileResolver.Resolve(_config);
        var options = new ChatOptions
        {
            ModelId = _config.Model,
            MaxOutputTokens = profile.MaxOutputTokens,
            Instructions = systemPrompt,
            Tools = ChatMessageConverter.ToMeaiTools(toolDefs),
            ToolMode = ChatToolMode.Auto,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatClientPropertyKeys.Effort] = profile.Effort.ToString(),
                [ChatClientPropertyKeys.ThinkingMode] = profile.ThinkingMode.ToString(),
                [ChatClientPropertyKeys.ApiRequestTimeout] = _config.ApiRequestTimeout,
                [ChatClientPropertyKeys.ApiMaxRetryCount] = _config.ApiMaxRetryCount,
                [ChatClientPropertyKeys.ApiRetryBaseDelay] = _config.ApiRetryBaseDelay,
                [ChatClientPropertyKeys.ApiRetryMaxDelay] = _config.ApiRetryMaxDelay,
                [ChatClientPropertyKeys.ApiRetryBackoffMultiplier] = _config.ApiRetryBackoffMultiplier,
            },
        };
        if (profile.ThinkingBudgetTokens is { } budget)
            options.AdditionalProperties[ChatClientPropertyKeys.ThinkingBudgetTokens] = budget;

        if (promptCachingEnabled)
        {
            options.RawRepresentationFactory = _ => new ApiMessageCreateParams
            {
                Model = _config.Model,
                MaxTokens = profile.MaxOutputTokens,
                System = CreateApiSystemPrompt(systemPrompt, hasSystemPromptCacheBreakpoint),
                Messages = ConvertToApiMessages(_messages, messagePromptCacheBreakpointBudget),
            };
        }

        return options;
    }

    private void UpdateQuotaStatus(ApiQuotaStatus? status)
    {
        if (status != null)
            LatestQuotaStatus = status;
    }

    private static ApiQuotaStatus? ExtractQuotaStatus(AdditionalPropertiesDictionary? additionalProperties)
    {
        if (additionalProperties == null ||
            !additionalProperties.TryGetValue(ChatClientPropertyKeys.ApiQuotaStatus, out var value) ||
            value is not ApiQuotaStatus status)
        {
            return null;
        }

        return status;
    }

    private static ApiQuotaStatus? TryGetQuotaStatus(Exception exception)
    {
        return exception.Data.Contains(ChatClientPropertyKeys.ApiQuotaStatus)
            ? exception.Data[ChatClientPropertyKeys.ApiQuotaStatus] as ApiQuotaStatus
            : null;
    }

    /// <summary>
    /// Clears messages.
    /// </summary>
    public async Task ClearMessagesAsync(CancellationToken ct = default)
    {
        await EndSessionAsync(dueToClear: true, ct);
        _messages.Clear();
        _totalUsage = TokenUsage.Empty;
        if (_journal != null)
            await _journal.ResetHeadAsync(ct);
    }

    public void ClearMessages() =>
        ClearMessagesAsync().GetAwaiter().GetResult();

    public async Task SetSessionTitleAsync(
        string? title,
        CancellationToken ct = default)
    {
        _sessionMetadata.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (_journal != null)
        {
            await _journal.UpdateMetadataAsync(
                metadata => metadata.Title = _sessionMetadata.Title,
                ct);
        }
    }

    public async Task AddSessionTagAsync(
        string tag,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        _sessionMetadata.Tags.Add(tag.Trim());
        if (_journal != null)
        {
            var tags = _sessionMetadata.Tags.ToArray();
            await _journal.UpdateMetadataAsync(
                metadata =>
                {
                    metadata.Tags.Clear();
                    foreach (var item in tags)
                        metadata.Tags.Add(item);
                },
                ct);
        }
    }

    public async Task RemoveSessionTagAsync(
        string tag,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        _sessionMetadata.Tags.Remove(tag.Trim());
        if (_journal != null)
        {
            var tags = _sessionMetadata.Tags.ToArray();
            await _journal.UpdateMetadataAsync(
                metadata =>
                {
                    metadata.Tags.Clear();
                    foreach (var item in tags)
                        metadata.Tags.Add(item);
                },
                ct);
        }
    }

    public async Task ClearSessionTagsAsync(CancellationToken ct = default)
    {
        _sessionMetadata.Tags.Clear();
        if (_journal != null)
        {
            await _journal.UpdateMetadataAsync(
                metadata => metadata.Tags.Clear(),
                ct);
        }
    }

    public IAttachmentRegistry Attachments => _attachmentRegistry;

    public async Task<Attachment> RegisterAttachmentAsync(
        string fileName,
        string mimeType,
        long sizeBytes,
        AttachmentSource source,
        string? sourcePath = null,
        CancellationToken ct = default)
    {
        var attachment = _attachmentRegistry.Register(fileName, mimeType, sizeBytes, source, sourcePath);
        _sessionMetadata.Attachments[attachment.Id] = attachment;
        if (_journal != null)
        {
            var snapshot = _sessionMetadata.Attachments.ToDictionary(kv => kv.Key, kv => kv.Value);
            await _journal.UpdateMetadataAsync(
                metadata =>
                {
                    metadata.Attachments.Clear();
                    foreach (var (id, a) in snapshot)
                        metadata.Attachments[id] = a;
                },
                ct);
        }

        await AddMessageAsync(
            new AttachmentMessage
            {
                Content = $"Attachment registered: {attachment.FileName}",
                AttachmentId = attachment.Id,
                AttachmentName = attachment.FileName,
                MediaType = attachment.MimeType,
                SourcePath = attachment.SourcePath,
                SizeBytes = attachment.SizeBytes,
            },
            ct);

        return attachment;
    }

    public async Task<bool> RemoveAttachmentAsync(
        string attachmentId,
        CancellationToken ct = default)
    {
        if (!_attachmentRegistry.Remove(attachmentId))
            return false;

        _sessionMetadata.Attachments.Remove(attachmentId);
        if (_journal != null)
        {
            var snapshot = _sessionMetadata.Attachments.ToDictionary(kv => kv.Key, kv => kv.Value);
            await _journal.UpdateMetadataAsync(
                metadata =>
                {
                    metadata.Attachments.Clear();
                    foreach (var (id, a) in snapshot)
                        metadata.Attachments[id] = a;
                },
                ct);
        }

        return true;
    }

    public async Task SetPermissionModeAsync(
        PermissionMode mode,
        CancellationToken ct = default)
    {
        var current = CurrentPermissionMode;
        if (mode == PermissionMode.Plan)
        {
            if (current != PermissionMode.Plan)
                _planModeResumeMode = current;
        }
        else
        {
            _planModeResumeMode = mode;
        }

        _contextProvider.PermissionContext.Mode = mode;
        _sessionMetadata.Mode = mode;
        if (_journal != null)
        {
            await _journal.UpdateMetadataAsync(
                metadata => metadata.Mode = mode,
                ct);
        }
    }

    public async Task<bool> EnterPlanModeAsync(CancellationToken ct = default)
    {
        if (IsPlanModeActive)
            return false;

        await SetPermissionModeAsync(PermissionMode.Plan, ct);
        return true;
    }

    public async Task<PermissionMode> ExitPlanModeAsync(CancellationToken ct = default)
    {
        var resumeMode = PlanModeResumeMode;
        if (IsPlanModeActive)
            await SetPermissionModeAsync(resumeMode, ct);

        return resumeMode;
    }

    public bool IsAwayModeActive => _awayModeActive;

    public DateTimeOffset? AwayEnteredAt => _awayEnteredAt;

    public string? AwayTriggerReason => _awayTriggerReason;

    public async Task<bool> EnterAwayModeAsync(string triggerReason, CancellationToken ct = default)
    {
        if (_awayModeActive)
            return false;

        var now = DateTimeOffset.UtcNow;
        _awayModeActive = true;
        _awayEnteredAt = now;
        _awayTriggerReason = triggerReason;

        _sessionMetadata.AwayEnteredAt = now;
        _sessionMetadata.AwayTriggerReason = triggerReason;

        if (_journal != null)
        {
            await _journal.AppendMetadataEntryAsync(
                new TranscriptMetadataEntry(
                    "away-enter",
                    System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        trigger = triggerReason,
                        entered_at = now,
                    }),
                    now),
                ct);
        }

        return true;
    }

    public async Task<SystemAwaySummaryMessage?> ExitAwayModeAsync(CancellationToken ct = default)
    {
        if (!_awayModeActive || !_awayEnteredAt.HasValue)
            return null;

        var now = DateTimeOffset.UtcNow;
        var enteredAt = _awayEnteredAt.Value;
        var duration = now - enteredAt;
        var reason = _awayTriggerReason ?? "unknown";

        var summary = new SystemAwaySummaryMessage
        {
            AwayEnteredAt = enteredAt,
            AwayExitedAt = now,
            TriggerReason = reason,
            SummaryText = $"User was away for {FormatDuration(duration)}. Reason: {reason}.",
        };

        await AddMessageAsync(summary, ct);

        if (_journal != null)
        {
            await _journal.AppendMetadataEntryAsync(
                new TranscriptMetadataEntry(
                    "away-exit",
                    System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        exited_at = now,
                        duration_ms = (long)duration.TotalMilliseconds,
                    }),
                    now),
                ct);
        }

        _awayModeActive = false;
        _awayEnteredAt = null;
        _awayTriggerReason = null;
        _sessionMetadata.AwayEnteredAt = null;
        _sessionMetadata.AwayTriggerReason = null;

        return summary;
    }

    public async Task EnqueueExternalMessageAsync(
        ConversationMessage message,
        CancellationToken ct = default)
    {
        _pendingExternalMessages.Enqueue(message);
        if (_journal != null)
        {
            await _journal.AppendMessageAsync(
                message,
                _contextProvider.WorkingDirectory,
                _config.Model,
                ct);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    }

    public async Task<ConversationCompactionResult?> CompactAsync(
        int preserveTailCount = 8,
        CancellationToken ct = default)
    {
        await _hooks.OnPreCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Conversation,
                automatic: false,
                reason: "manual",
                preserveTailCount: preserveTailCount,
                messageCount: _messages.Count),
            ct);

        var result = _compactor.Compact(_messages, preserveTailCount);
        if (result == null)
            return null;

        await ApplyCompactionResultAsync(
            result,
            automatic: false,
            reason: "manual",
            mode: "compact",
            ct);
        await _hooks.OnPostCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Conversation,
                automatic: false,
                reason: "manual",
                preserveTailCount: preserveTailCount,
                messageCount: _messages.Count,
                conversationResult: result),
            ct);
        return result;
    }

    public async Task<ConversationCompactionResult?> CompactUpToAsync(
        int upToIndex,
        CancellationToken ct = default)
    {
        await _hooks.OnPreCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Conversation,
                automatic: false,
                reason: "manual",
                preserveTailCount: upToIndex,
                messageCount: _messages.Count),
            ct);

        var result = _compactor.CompactUpTo(_messages, upToIndex);
        if (result == null)
            return null;

        await ApplyCompactionResultAsync(
            result,
            automatic: false,
            reason: "manual",
            mode: "compact_up_to",
            ct);
        await _hooks.OnPostCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Conversation,
                automatic: false,
                reason: "manual",
                preserveTailCount: upToIndex,
                messageCount: _messages.Count,
                conversationResult: result),
            ct);
        return result;
    }

    public async Task<ConversationCompactionResult?> CompactFromAsync(
        int fromIndex,
        CancellationToken ct = default)
    {
        await _hooks.OnPreCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Conversation,
                automatic: false,
                reason: "manual",
                preserveTailCount: fromIndex,
                messageCount: _messages.Count),
            ct);

        var result = _compactor.CompactFrom(_messages, fromIndex);
        if (result == null)
            return null;

        await ApplyCompactionResultAsync(
            result,
            automatic: false,
            reason: "manual",
            mode: "compact_from",
            ct);
        await _hooks.OnPostCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Conversation,
                automatic: false,
                reason: "manual",
                preserveTailCount: fromIndex,
                messageCount: _messages.Count,
                conversationResult: result),
            ct);
        return result;
    }

    public async Task<SessionMemoryCompactionResult?> SessionMemoryCompactAsync(
        int preserveTailCount = 8,
        CancellationToken ct = default)
    {
        await _hooks.OnPreCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.SessionMemory,
                automatic: false,
                reason: "manual",
                preserveTailCount: preserveTailCount,
                messageCount: _messages.Count),
            ct);

        var result = _sessionMemoryCompactor.Compact(
            _messages,
            new SessionMemoryCompactionOptions
            {
                PreserveTailCount = preserveTailCount,
            });

        if (result == null || !result.HasChanges)
            return null;

        await ApplySessionMemoryCompactionResultAsync(
            result,
            automatic: false,
            reason: "manual",
            ct);
        await _hooks.OnPostCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.SessionMemory,
                automatic: false,
                reason: "manual",
                preserveTailCount: preserveTailCount,
                messageCount: _messages.Count,
                sessionMemoryResult: result),
            ct);
        return result;
    }

    public async Task<MicrocompactResult?> MicrocompactAsync(
        int preserveTailCount = 8,
        bool force = true,
        CancellationToken ct = default)
    {
        await _hooks.OnPreCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Microcompact,
                automatic: false,
                reason: "manual",
                preserveTailCount: preserveTailCount,
                messageCount: _messages.Count),
            ct);

        var result = _microCompactor.Run(
            _messages,
            new MicrocompactRunOptions
            {
                PreserveTailCount = preserveTailCount,
                Force = force,
            });

        if (!result.HasChanges)
            return null;

        await ApplyMicrocompactResultAsync(
            result,
            automatic: false,
            reason: "manual",
            ct);
        await _hooks.OnPostCompactAsync(
            new CompactHookContext(
                CompactionLifecycleKind.Microcompact,
                automatic: false,
                reason: "manual",
                preserveTailCount: preserveTailCount,
                messageCount: _messages.Count,
                microcompactResult: result),
            ct);
        return result;
    }

    private async Task<IReadOnlyList<QueryEvent>> PrepareContextForTurnAsync(
        CancellationToken ct)
    {
        var events = new List<QueryEvent>();

        if (!_config.EnableAutoCompact)
            return events;

        if (_consecutiveAutoCompactFailures >= _config.AutoCompactFailureLimit)
        {
            events.Add(new ContextCompactionEvent
            {
                Mode = "skipped",
                Automatic = true,
                Reason = $"auto-compact circuit open after {_consecutiveAutoCompactFailures} consecutive failures",
            });
            return events;
        }

        try
        {
            var preparation = _contextPressurePipeline.Prepare(
                _messages,
                new ContextPressureOptions
                {
                    EnableAutoCompact = _config.EnableAutoCompact,
                    EnableSessionMemoryCompact = _config.EnableSessionMemoryCompact,
                    PreserveTailCount = _config.AutoCompactPreserveTailCount,
                    Policy = new AutoCompactPolicyOptions
                    {
                        ApproxContextWindowTokens = _config.ApproxContextWindowTokens,
                        MaxOutputTokens = _config.MaxTokens,
                        BufferTokens = _config.AutoCompactBufferTokens,
                        ApproxCharsPerToken = _config.ApproxCharsPerToken,
                        MinimumMessageCount = _config.AutoCompactMinimumMessageCount,
                        WarningRatio = _config.AutoCompactWarningRatio,
                        BlockingRatio = _config.AutoCompactBlockingRatio,
                    },
                    SessionMemory = new SessionMemoryCompactionOptions
                    {
                        PreserveTailCount = _config.AutoCompactPreserveTailCount,
                    },
                });

            if (preparation.MicrocompactResult is { HasChanges: true } microcompact)
            {
                await _hooks.OnPreCompactAsync(
                    new CompactHookContext(
                        CompactionLifecycleKind.Microcompact,
                        automatic: true,
                        reason: preparation.InitialDecision.Reason,
                        preserveTailCount: _config.AutoCompactPreserveTailCount,
                        messageCount: _messages.Count),
                    ct);

                await ApplyMicrocompactResultAsync(
                    microcompact,
                    automatic: true,
                    reason: preparation.InitialDecision.Reason,
                    ct);
                await _hooks.OnPostCompactAsync(
                    new CompactHookContext(
                        CompactionLifecycleKind.Microcompact,
                        automatic: true,
                        reason: preparation.InitialDecision.Reason,
                        preserveTailCount: _config.AutoCompactPreserveTailCount,
                        messageCount: _messages.Count,
                        microcompactResult: microcompact),
                    ct);
                events.Add(new ContextCompactionEvent
                {
                    Mode = "microcompact",
                    Automatic = true,
                    Reason = preparation.InitialDecision.Reason,
                    ClearedToolResults = microcompact.ClearedToolResultCount,
                    ClearedThinkingBlocks = microcompact.ClearedThinkingBlockCount,
                });
            }

            if (preparation.SessionMemoryResult is { HasChanges: true } sessionMemory)
            {
                await _hooks.OnPreCompactAsync(
                    new CompactHookContext(
                        CompactionLifecycleKind.SessionMemory,
                        automatic: true,
                        reason: preparation.InitialDecision.Reason,
                        preserveTailCount: _config.AutoCompactPreserveTailCount,
                        messageCount: _messages.Count),
                    ct);

                await ApplySessionMemoryCompactionResultAsync(
                    sessionMemory,
                    automatic: true,
                    reason: preparation.InitialDecision.Reason,
                    ct);
                await _hooks.OnPostCompactAsync(
                    new CompactHookContext(
                        CompactionLifecycleKind.SessionMemory,
                        automatic: true,
                        reason: preparation.InitialDecision.Reason,
                        preserveTailCount: _config.AutoCompactPreserveTailCount,
                        messageCount: _messages.Count,
                        sessionMemoryResult: sessionMemory),
                    ct);
                events.Add(new ContextCompactionEvent
                {
                    Mode = "session_memory",
                    Automatic = true,
                    Reason = preparation.InitialDecision.Reason,
                    RemovedMessages = sessionMemory.FoldedMessageCount,
                    PreservedMessages = sessionMemory.ActiveMessages.Count - 1,
                });
            }

            if (preparation.CompactionResult != null)
            {
                await _hooks.OnPreCompactAsync(
                    new CompactHookContext(
                        CompactionLifecycleKind.Conversation,
                        automatic: true,
                        reason: preparation.InitialDecision.Reason,
                        preserveTailCount: _config.AutoCompactPreserveTailCount,
                        messageCount: _messages.Count),
                    ct);

                await ApplyCompactionResultAsync(
                    preparation.CompactionResult,
                    automatic: true,
                    reason: preparation.InitialDecision.Reason,
                    mode: "compact",
                    ct);
                await _hooks.OnPostCompactAsync(
                    new CompactHookContext(
                        CompactionLifecycleKind.Conversation,
                        automatic: true,
                        reason: preparation.InitialDecision.Reason,
                        preserveTailCount: _config.AutoCompactPreserveTailCount,
                        messageCount: _messages.Count,
                        conversationResult: preparation.CompactionResult),
                    ct);
                events.Add(new ContextCompactionEvent
                {
                    Mode = "compact",
                    Automatic = true,
                    Reason = preparation.InitialDecision.Reason,
                    RemovedMessages = preparation.CompactionResult.RemovedMessageCount,
                    PreservedMessages = preparation.CompactionResult.ActiveMessages.Count - 1,
                });
            }

            _consecutiveAutoCompactFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveAutoCompactFailures++;
            events.Add(new ContextCompactionEvent
            {
                Mode = "failed",
                Automatic = true,
                Reason = ex.Message,
            });
        }

        return events;
    }

    private async Task ApplyCompactionResultAsync(
        ConversationCompactionResult result,
        bool automatic,
        string reason,
        string mode,
        CancellationToken ct)
    {
        await ApplyCheckpointAsync(
            result.SummaryMessage,
            result.ActiveMessages,
            CreateCompactBoundaryMessage(
                summaryMessageId: result.SummaryMessage.Id,
                mode: mode,
                automatic: automatic,
                reason: reason,
                foldedMessageCount: result.RemovedMessageCount,
                preservedMessageCount: result.ActiveMessages.Count - 1),
            ct);
    }

    private async Task ApplySessionMemoryCompactionResultAsync(
        SessionMemoryCompactionResult result,
        bool automatic,
        string reason,
        CancellationToken ct)
    {
        await ApplyCheckpointAsync(
            result.MemoryMessage,
            result.ActiveMessages,
            CreateCompactBoundaryMessage(
                summaryMessageId: result.MemoryMessage.Id,
                mode: "session_memory",
                automatic: automatic,
                reason: reason,
                foldedMessageCount: result.FoldedMessageCount,
                preservedMessageCount: result.ActiveMessages.Count - 1),
            ct);
        if (_sessionMemoryFile != null)
        {
            await _sessionMemoryFile.SaveAsync(result.SummaryText, ct);
            await AddMessageAsync(
                new SystemMemorySavedMessage
                {
                    Content = "Session memory summary saved.",
                    MemoryKind = "session",
                    FilePath = _sessionMemoryFile.Path,
                    CharacterCount = result.SummaryText.Length,
                },
                ct);
        }

        _contextProvider.SessionMemoryContent = result.SummaryText;
    }

    private async Task ApplyMicrocompactResultAsync(
        MicrocompactResult result,
        bool automatic,
        string reason,
        CancellationToken ct)
    {
        _messages.Clear();
        _messages.AddRange(result.UpdatedMessages);

        if (_journal != null)
        {
            await _journal.RecordMicrocompactAsync(
                result.Edits,
                _contextProvider.WorkingDirectory,
                _config.Model,
                ct);
        }

        await AddMessageAsync(
            CreateMicrocompactBoundaryMessage(
                automatic,
                reason,
                result.ClearedToolResultCount,
                result.ClearedThinkingBlockCount),
            ct);
    }

    private async Task ApplyCheckpointAsync(
        ConversationMessage summaryMessage,
        IReadOnlyList<ConversationMessage> activeMessages,
        SystemCompactBoundaryMessage? boundaryMessage,
        CancellationToken ct)
    {
        var activeSnapshot = InsertBoundaryMessage(activeMessages, summaryMessage, boundaryMessage);
        _messages.Clear();
        _messages.AddRange(activeSnapshot);

        if (_journal != null)
        {
            if (boundaryMessage != null)
            {
                await _journal.AppendMessageAsync(
                    boundaryMessage,
                    _contextProvider.WorkingDirectory,
                    _config.Model,
                    ct);
            }

            await _journal.RecordConversationCheckpointAsync(
                summaryMessage,
                activeSnapshot,
                _contextProvider.WorkingDirectory,
                _config.Model,
                ct);
        }
    }

    private async Task AddMessageAsync(
        ConversationMessage message,
        CancellationToken ct)
    {
        _messages.Add(message);
        if (_journal != null)
        {
            await _journal.AppendMessageAsync(
                message,
                _contextProvider.WorkingDirectory,
                _config.Model,
                ct);
        }
    }

    private async Task AddRuntimeMessagesAsync(
        IEnumerable<ConversationMessage> messages,
        CancellationToken ct)
    {
        foreach (var message in messages)
            await AddMessageAsync(message, ct);
    }

    private void FlushPendingExternalMessages()
    {
        while (_pendingExternalMessages.TryDequeue(out var message))
            _messages.Add(message);
    }

    private async Task PersistRuntimeStateAsync(CancellationToken ct)
    {
        if (_journal == null)
            return;

        await _journal.UpdateSessionInfoAsync(
            _contextProvider.WorkingDirectory,
            _config.Model,
            ct);
    }

    private PermissionMode CurrentPermissionMode =>
        _sessionMetadata.Mode ?? _contextProvider.PermissionContext.Mode;

    private PermissionMode ResolveInitialPlanModeResumeMode()
    {
        var current = CurrentPermissionMode;
        return current == PermissionMode.Plan
            ? PermissionMode.Default
            : current;
    }

    private IReadOnlyList<ITool> GetAvailableTools() =>
        _tools.GetEnabledTools(IsToolAllowedInCurrentMode);

    private Task<IReadOnlyList<JsonElement>> GetAvailableToolDefinitionsAsync() =>
        _tools.GetToolDefinitionsAsync(IsToolAllowedInCurrentMode);

    private bool IsToolAllowedInCurrentMode(ITool tool) =>
        !IsPlanModeActive || PlanModeToolPolicy.IsAllowedInPlanMode(tool.Name);

    private async Task CollectAssistantTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        AssistantTurnAccumulator turn,
        CancellationToken ct)
    {
        var response = await _chatClient.GetResponseAsync(messages, options, ct);
        UpdateQuotaStatus(ExtractQuotaStatus(response.AdditionalProperties));
        ChatMessageConverter.PopulateAssistantTurn(response, turn);
        turn.StopReason = NormalizeStopReason(turn.StopReason);
    }

    private static IReadOnlyList<QueryEvent> EmitBufferedAssistantTurnEvents(
        AssistantTurnAccumulator turn)
    {
        var events = new List<QueryEvent>();
        foreach (var block in turn.ContentBlocks)
        {
            switch (block)
            {
                case TextBlock text:
                    events.Add(new TextDeltaEvent(text.Text));
                    break;
                case ThinkingBlock thinking:
                    events.Add(new ThinkingDeltaEvent(thinking.Text));
                    break;
                case ToolUseBlock toolUse:
                    events.Add(new ToolUseStartEvent(
                        toolUse.ToolUseId,
                        toolUse.Name,
                        toolUse.Input));
                    break;
            }
        }

        return events;
    }

    private async IAsyncEnumerable<QueryEvent> StreamAssistantTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        AssistantTurnAccumulator turn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var accumulator = new StreamingChatContentAccumulator(turn);
        var updates = new List<ChatResponseUpdate>();
        var emittedMessageStart = false;

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, options, ct))
        {
            updates.Add(update);
            UpdateQuotaStatus(ExtractQuotaStatus(update.AdditionalProperties));

            IList<AIContent> contents = update.Contents ?? [];
            var fallbackEvents = accumulator.TrackRawUpdate(update.RawRepresentation, contents);
            var messageId = !string.IsNullOrWhiteSpace(update.MessageId)
                ? update.MessageId
                : update.ResponseId;

            if (!emittedMessageStart && !string.IsNullOrWhiteSpace(messageId))
            {
                turn.MessageId = messageId;
                emittedMessageStart = true;
                yield return new MessageStartEvent(messageId);
            }
            else if (!string.IsNullOrWhiteSpace(messageId))
            {
                turn.MessageId ??= messageId;
            }

            if (update.FinishReason is { } finishReason)
                turn.StopReason = ToStopReason(finishReason);

            foreach (var content in contents)
            {
                if (content is UsageContent usageContent)
                {
                    turn.Usage = ChatMessageConverter.ToTokenUsage(usageContent.Details);
                    continue;
                }

                foreach (var queryEvent in accumulator.Apply(content))
                    yield return queryEvent;
            }

            foreach (var fallbackEvent in fallbackEvents)
                yield return fallbackEvent;
        }

        foreach (var fallbackEvent in accumulator.FlushPendingToolUses())
            yield return fallbackEvent;
        accumulator.Flush();

        if (updates.Count == 0)
            yield break;

        var response = updates.ToChatResponse();
        turn.MessageId ??= response.Messages
            .Select(message => message.MessageId)
            .FirstOrDefault(messageId => !string.IsNullOrWhiteSpace(messageId))
            ?? response.ResponseId;
        turn.StopReason ??= NormalizeStopReason(response.FinishReason?.Value);
        turn.Usage ??= ChatMessageConverter.ToTokenUsage(response.Usage);
    }

    private async Task EnsureSessionStartedAsync(CancellationToken ct)
    {
        if (_sessionStarted)
            return;

        _sessionStarted = true;
        _sessionEnded = false;

        await _hooks.OnSessionStartAsync(
            new SessionHookContext(
                SessionId,
                _contextProvider.WorkingDirectory,
                _config.Model,
                _sessionMetadata,
                _messages.Count),
            ct);
    }

    private async Task EndSessionAsync(bool dueToClear, CancellationToken ct)
    {
        if (!_sessionStarted || _sessionEnded)
            return;

        _sessionEnded = true;
        await _hooks.OnSessionEndAsync(
            new SessionEndHookContext(
                SessionId,
                _contextProvider.WorkingDirectory,
                _config.Model,
                _sessionMetadata,
                _messages.Count,
                dueToClear),
            ct);

        if (dueToClear)
            _sessionStarted = false;
    }

    private StopHookContext BuildStopHookContext(
        bool success,
        string? errorMessage,
        TimeSpan duration,
        int turnCount) =>
        new(
            SessionId,
            _contextProvider.WorkingDirectory,
            _config.Model,
            success,
            errorMessage,
            duration,
            turnCount,
            _totalUsage);

    public async ValueTask DisposeAsync()
    {
        await EndSessionAsync(dueToClear: false, CancellationToken.None);
    }

    private static TokenUsage CreateTokenUsage(
        long? inputTokens,
        long? outputTokens,
        long? cacheReadInputTokens,
        long? cacheCreationInputTokens) =>
        new()
        {
            InputTokens = (int)(inputTokens ?? 0),
            OutputTokens = (int)(outputTokens ?? 0),
            CacheReadInputTokens = (int)(cacheReadInputTokens ?? 0),
            CacheCreationInputTokens = (int)(cacheCreationInputTokens ?? 0),
        };

    private IReadOnlyList<ConversationMessage> InsertBoundaryMessage(
        IReadOnlyList<ConversationMessage> activeMessages,
        ConversationMessage summaryMessage,
        SystemCompactBoundaryMessage? boundaryMessage)
    {
        if (boundaryMessage == null)
            return activeMessages.ToArray();

        if (activeMessages.Count > 0 &&
            string.Equals(activeMessages[0].Id, summaryMessage.Id, StringComparison.Ordinal))
        {
            return [summaryMessage, boundaryMessage, .. activeMessages.Skip(1)];
        }

        return [boundaryMessage, .. activeMessages];
    }

    private SystemCompactBoundaryMessage CreateCompactBoundaryMessage(
        string summaryMessageId,
        string mode,
        bool automatic,
        string reason,
        int foldedMessageCount,
        int preservedMessageCount) =>
        new()
        {
            Content = $"Compaction boundary recorded for {mode}.",
            BoundaryId = Guid.NewGuid().ToString("N"),
            Mode = mode,
            Automatic = automatic,
            Reason = reason,
            FoldedMessageCount = Math.Max(0, foldedMessageCount),
            PreservedMessageCount = Math.Max(0, preservedMessageCount),
            SummaryMessageId = summaryMessageId,
        };

    private SystemMicrocompactBoundaryMessage CreateMicrocompactBoundaryMessage(
        bool automatic,
        string reason,
        int clearedToolResultCount,
        int clearedThinkingBlockCount) =>
        new()
        {
            Content = "Microcompact boundary recorded.",
            BoundaryId = Guid.NewGuid().ToString("N"),
            Automatic = automatic,
            Reason = reason,
            ClearedToolResultCount = Math.Max(0, clearedToolResultCount),
            ClearedThinkingBlockCount = Math.Max(0, clearedThinkingBlockCount),
        };

    private SystemPermissionRetryMessage CreatePermissionRetryMessage(
        ToolPermissionRequestUpdate request) =>
        new()
        {
            Content = $"Permission requested for {request.Invocation.Name}.",
            ToolName = request.Invocation.Name,
            ToolUseId = request.Invocation.ToolUseId,
            Attempt = CountPermissionRetries(request.Invocation.ToolUseId) + 1,
            Reason = request.Description,
            UpdatedInput = request.ObservedInput.Clone(),
        };

    private int CountPermissionRetries(string toolUseId) =>
        _messages
            .OfType<SystemPermissionRetryMessage>()
            .Count(message => string.Equals(message.ToolUseId, toolUseId, StringComparison.Ordinal));

    private ToolUseSummaryMessage CreateToolUseSummaryMessage(ToolRunOutcome outcome) =>
        new()
        {
            Content = $"Tool {outcome.Invocation.Name} completed.",
            ToolUseId = outcome.Invocation.ToolUseId,
            ToolName = outcome.Invocation.Name,
            IsError = outcome.Result.IsError,
            ResultPreview = BuildResultPreview(outcome.Result.Data),
        };

    private IEnumerable<ConversationMessage> CreateStopLifecycleMessages(
        bool success,
        TimeSpan duration,
        int turnCount,
        string? stopReason,
        string? errorMessage)
    {
        yield return new SystemTurnDurationMessage
        {
            Content = "Turn duration recorded.",
            TurnCount = turnCount,
            DurationMs = duration.TotalMilliseconds,
            Model = _config.Model,
        };

        yield return new SystemApiMetricsMessage
        {
            Content = "API metrics recorded.",
            Model = _config.Model,
            Usage = _totalUsage,
            DurationMs = duration.TotalMilliseconds,
            StopReason = stopReason,
            Success = success,
        };

        yield return new SystemStopHookSummaryMessage
        {
            Content = success
                ? "Stop hooks completed."
                : "Stop-failure hooks completed.",
            HookEvent = success ? "stop" : "stop_failure",
            Success = success,
            DurationMs = duration.TotalMilliseconds,
            Summary = success
                ? "Stop hook lifecycle finished successfully."
                : "Stop hook lifecycle finished after a failed turn.",
            ErrorMessage = errorMessage,
        };
    }

    private static string BuildResultPreview(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return string.Empty;

        var collapsed = string.Join(
            ' ',
            result.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= 160
            ? collapsed
            : $"{collapsed[..157]}...";
    }

    private PromptCacheStatusEvent? CreatePromptCacheStatusEvent(
        bool promptCachingEnabled,
        TokenUsage usage)
    {
        if (!promptCachingEnabled)
        {
            _promptCacheWarm = false;
            return null;
        }

        var hasCacheActivity =
            usage.CacheReadInputTokens > 0 ||
            usage.CacheCreationInputTokens > 0;
        var breakDetected = _promptCacheWarm && usage.CacheReadInputTokens == 0;
        _promptCacheWarm = hasCacheActivity;

        return breakDetected
            ? new PromptCacheStatusEvent(usage, BreakDetected: true)
            : null;
    }

    private static ApiCacheControlEphemeral CreatePromptCacheControl() => new();

    private static int GetStablePromptCacheMessageCount(
        IReadOnlyList<ConversationMessage> messages)
    {
        if (messages.Count == 0)
            return 0;

        return messages[^1] is UserMessage user &&
               user.Content.Any(block => block is not ToolResultBlock)
            ? messages.Count - 1
            : messages.Count;
    }

    private static void RegisterPromptCacheCandidate(
        List<PromptCacheBlockCandidate> candidates,
        bool includeInStablePrefix,
        ref int contentPosition,
        Action? applyCacheControl)
    {
        if (!includeInStablePrefix)
            return;

        if (applyCacheControl != null)
            candidates.Add(new PromptCacheBlockCandidate(contentPosition, applyCacheControl));

        contentPosition++;
    }

    private static void ApplyPromptCacheBreakpoints(
        IReadOnlyList<PromptCacheBlockCandidate> candidates,
        int breakpointBudget)
    {
        if (breakpointBudget <= 0 || candidates.Count == 0)
            return;

        var current = candidates[^1];
        current.ApplyCacheControl();
        var applied = 1;

        while (applied < breakpointBudget && current.Position >= PromptCacheLookbackWindowBlocks)
        {
            var lowerBound = current.Position - (PromptCacheLookbackWindowBlocks - 1);
            var next = candidates.FirstOrDefault(candidate =>
                candidate.Position >= lowerBound &&
                candidate.Position < current.Position);
            if (next == null)
                break;

            next.ApplyCacheControl();
            current = next;
            applied++;
        }
    }

    private static bool SupportsPromptCaching(string? modelOrAlias)
    {
        var descriptor = ClaudeModelCatalog.TryResolve(modelOrAlias);
        return descriptor?.StableId is
            "claude-3-5-haiku" or
            "claude-3-7-sonnet" or
            "claude-haiku-4-5" or
            "claude-sonnet-4" or
            "claude-sonnet-4-5" or
            "claude-sonnet-4-6" or
            "claude-opus-4" or
            "claude-opus-4-1" or
            "claude-opus-4-5" or
            "claude-opus-4-6";
    }

    private sealed record PendingApiMessage(ApiRole Role, List<ApiContentBlockParam> Content);

    private sealed record PromptCacheBlockCandidate(int Position, Action ApplyCacheControl);

    private sealed class StreamingChatContentAccumulator
    {
        private readonly AssistantTurnAccumulator _turn;
        private readonly Dictionary<long, PendingToolUse> _pendingToolUses = [];
        private BufferedContentKind _bufferKind;
        private StringBuilder? _textBuffer;
        private StringBuilder? _thinkingBuffer;
        private string? _thinkingSignature;

        public StreamingChatContentAccumulator(AssistantTurnAccumulator turn)
        {
            _turn = turn;
        }

        public IReadOnlyList<QueryEvent> Apply(AIContent content)
        {
            switch (content)
            {
                case TextContent text:
                    EnsureBuffer(BufferedContentKind.Text);
                    if (!string.IsNullOrEmpty(text.Text))
                        _textBuffer!.Append(text.Text);
                    return string.IsNullOrEmpty(text.Text) ? [] : [new TextDeltaEvent(text.Text)];

                case TextReasoningContent reasoning:
                    EnsureBuffer(BufferedContentKind.Thinking);
                    if (!string.IsNullOrEmpty(reasoning.Text))
                        _thinkingBuffer!.Append(reasoning.Text);
                    if (!string.IsNullOrWhiteSpace(reasoning.ProtectedData))
                        _thinkingSignature = reasoning.ProtectedData;
                    return string.IsNullOrEmpty(reasoning.Text) ? [] : [new ThinkingDeltaEvent(reasoning.Text)];

                case FunctionCallContent functionCall:
                    Flush();
                    var toolUse = new ToolUseBlock
                    {
                        ToolUseId = functionCall.CallId ?? string.Empty,
                        Name = functionCall.Name ?? string.Empty,
                        Input = functionCall.Arguments != null
                            ? JsonSerializer.SerializeToElement(functionCall.Arguments)
                            : JsonSerializer.SerializeToElement(new { }),
                    };

                    MarkToolUseEmitted(toolUse.ToolUseId);
                    return AddToolUse(toolUse);

                default:
                    return [];
            }
        }

        public IReadOnlyList<QueryEvent> TrackRawUpdate(
            object? rawRepresentation,
            IList<AIContent> contents)
        {
            if (rawRepresentation is ApiRawMessageStreamEvent streamEvent)
                rawRepresentation = streamEvent.Value;

            if (rawRepresentation is ApiRawContentBlockStartEvent startEvent)
            {
                if (startEvent.ContentBlock.TryPickToolUse(out var toolUseBlock))
                {
                    _pendingToolUses[startEvent.Index] = new PendingToolUse(
                        startEvent.Index,
                        toolUseBlock.ID ?? string.Empty,
                        toolUseBlock.Name ?? string.Empty,
                        toolUseBlock.Input is { Count: > 0 }
                            ? JsonSerializer.SerializeToElement(toolUseBlock.Input)
                            : JsonSerializer.SerializeToElement(new { }));
                }

                return [];
            }

            if (rawRepresentation is ApiRawContentBlockDeltaEvent deltaEvent &&
                deltaEvent.Delta.TryPickInputJson(out var inputJsonDelta) &&
                _pendingToolUses.TryGetValue(deltaEvent.Index, out var pending))
            {
                pending.PartialJson.Append(inputJsonDelta.PartialJson);
                return [];
            }

            if (rawRepresentation is ApiRawContentBlockStopEvent stopEvent)
            {
                var hasFunctionCall = contents.OfType<FunctionCallContent>().Any();
                if (_pendingToolUses.Remove(stopEvent.Index, out var stoppedPending) && !hasFunctionCall)
                    return FinalizePendingToolUse(stoppedPending);

                return [];
            }

            if (rawRepresentation is JsonElement jsonEvent)
                return TrackJsonRawUpdate(jsonEvent, contents);

            if (rawRepresentation is JsonDocument jsonDocument)
                return TrackJsonRawUpdate(jsonDocument.RootElement, contents);

            return [];
        }

        private IReadOnlyList<QueryEvent> TrackJsonRawUpdate(
            JsonElement rawEvent,
            IList<AIContent> contents)
        {
            if (rawEvent.ValueKind != JsonValueKind.Object ||
                !rawEvent.TryGetProperty("type", out var typeProperty))
                return [];

            var eventType = typeProperty.GetString();
            if (string.IsNullOrWhiteSpace(eventType))
                return [];

            switch (eventType)
            {
                case "content_block_start":
                    if (!TryGetIndex(rawEvent, out var startIndex) ||
                        !rawEvent.TryGetProperty("content_block", out var contentBlock) ||
                        !contentBlock.TryGetProperty("type", out var blockTypeProperty) ||
                        !string.Equals(blockTypeProperty.GetString(), "tool_use", StringComparison.Ordinal))
                    {
                        return [];
                    }

                    _pendingToolUses[startIndex] = new PendingToolUse(
                        startIndex,
                        contentBlock.TryGetProperty("id", out var idProperty)
                            ? idProperty.GetString() ?? string.Empty
                            : string.Empty,
                        contentBlock.TryGetProperty("name", out var nameProperty)
                            ? nameProperty.GetString() ?? string.Empty
                            : string.Empty,
                        contentBlock.TryGetProperty("input", out var inputProperty)
                            ? inputProperty.Clone()
                            : JsonSerializer.SerializeToElement(new { }));
                    return [];

                case "content_block_delta":
                    if (!TryGetIndex(rawEvent, out var deltaIndex) ||
                        !_pendingToolUses.TryGetValue(deltaIndex, out var pending) ||
                        !rawEvent.TryGetProperty("delta", out var deltaPayload) ||
                        !deltaPayload.TryGetProperty("type", out var deltaTypeProperty) ||
                        !string.Equals(deltaTypeProperty.GetString(), "input_json_delta", StringComparison.Ordinal))
                    {
                        return [];
                    }

                    if (deltaPayload.TryGetProperty("partial_json", out var partialJsonProperty))
                        pending.PartialJson.Append(partialJsonProperty.GetString());

                    return [];

                case "content_block_stop":
                    if (!TryGetIndex(rawEvent, out var stopIndex))
                        return [];

                    var hasFunctionCall = contents.OfType<FunctionCallContent>().Any();
                    if (_pendingToolUses.Remove(stopIndex, out var stoppedPending) && !hasFunctionCall)
                        return FinalizePendingToolUse(stoppedPending);

                    return [];

                default:
                    return [];
            }
        }

        private static bool TryGetIndex(JsonElement rawEvent, out long index)
        {
            index = default;
            return rawEvent.TryGetProperty("index", out var indexProperty) &&
                   indexProperty.TryGetInt64(out index);
        }

        public IReadOnlyList<QueryEvent> FlushPendingToolUses()
        {
            if (_pendingToolUses.Count == 0)
                return [];

            var events = new List<QueryEvent>();
            foreach (var pending in _pendingToolUses.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            {
                foreach (var queryEvent in FinalizePendingToolUse(pending))
                    events.Add(queryEvent);
            }

            _pendingToolUses.Clear();
            return events;
        }

        public void Flush()
        {
            switch (_bufferKind)
            {
                case BufferedContentKind.Text when _textBuffer is { Length: > 0 }:
                    _turn.ContentBlocks.Add(new TextBlock(_textBuffer.ToString()));
                    break;

                case BufferedContentKind.Thinking
                    when _thinkingBuffer is { Length: > 0 } || !string.IsNullOrWhiteSpace(_thinkingSignature):
                    _turn.ContentBlocks.Add(new ThinkingBlock(
                        _thinkingBuffer?.ToString() ?? string.Empty,
                        _thinkingSignature));
                    break;
            }

            _bufferKind = BufferedContentKind.None;
            _textBuffer = null;
            _thinkingBuffer = null;
            _thinkingSignature = null;
        }

        private void EnsureBuffer(BufferedContentKind kind)
        {
            if (_bufferKind == kind)
                return;

            Flush();
            _bufferKind = kind;

            if (kind == BufferedContentKind.Text)
                _textBuffer = new StringBuilder();
            else if (kind == BufferedContentKind.Thinking)
                _thinkingBuffer = new StringBuilder();
        }

        private IReadOnlyList<QueryEvent> FinalizePendingToolUse(PendingToolUse pending)
        {
            if (pending.Emitted)
                return [];

            return AddToolUse(new ToolUseBlock
            {
                ToolUseId = pending.ToolUseId,
                Name = pending.Name,
                Input = pending.BuildInput(),
            });
        }

        private IReadOnlyList<QueryEvent> AddToolUse(ToolUseBlock toolUse)
        {
            _turn.ContentBlocks.Add(toolUse);
            _turn.ToolUseBlocks.Add(toolUse);
            return
            [
                new ToolUseStartEvent(
                    toolUse.ToolUseId,
                    toolUse.Name,
                    toolUse.Input),
            ];
        }

        private void MarkToolUseEmitted(string toolUseId)
        {
            foreach (var pending in _pendingToolUses.Values)
            {
                if (string.Equals(pending.ToolUseId, toolUseId, StringComparison.Ordinal))
                {
                    pending.Emitted = true;
                    break;
                }
            }
        }

        private enum BufferedContentKind
        {
            None,
            Text,
            Thinking,
        }

        private sealed class PendingToolUse(
            long index,
            string toolUseId,
            string name,
            JsonElement initialInput)
        {
            public long Index { get; } = index;
            public string ToolUseId { get; } = toolUseId;
            public string Name { get; } = name;
            public JsonElement InitialInput { get; } = initialInput.Clone();
            public StringBuilder PartialJson { get; } = new();
            public bool Emitted { get; set; }

            public JsonElement BuildInput()
            {
                if (PartialJson.Length > 0)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(PartialJson.ToString());
                        return document.RootElement.Clone();
                    }
                    catch
                    {
                        // Fall back to the initial payload if the partial JSON is malformed.
                    }
                }

                return InitialInput.Clone();
            }
        }
    }

    private static TokenUsage ComputeTotalUsage(
        IReadOnlyList<ConversationMessage> messages)
    {
        var total = TokenUsage.Empty;
        foreach (var assistant in messages.OfType<AssistantMessage>())
        {
            if (assistant.Usage != null)
                total += assistant.Usage;
        }

        return total;
    }
}
