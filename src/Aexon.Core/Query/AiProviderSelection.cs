namespace Aexon.Core.Query;

/// <summary>
/// Defines the supported AI providers for chat-client selection.
/// </summary>
public enum AiProvider
{
    Anthropic,
    OpenAI,
    Ollama,
}

/// <summary>
/// Represents the resolved provider/model pair for a session.
/// </summary>
public sealed record AiSessionTarget(
    AiProvider Provider,
    string Model);

/// <summary>
/// Represents the fully-resolved plan for a session: the provider/model target
/// plus any active NyxID AI-Service proxy routing derived from stored defaults.
/// </summary>
public sealed record AiSessionPlan(
    AiSessionTarget Target,
    string? ProxyServiceSlug,
    string? ProxyServiceLabel);

/// <summary>
/// Centralizes provider detection and model resolution rules.
/// </summary>
public static class AiProviderSelection
{
    /// <summary>
    /// Indicates the caller has configured a NyxID AI Service (e.g. Chrono
    /// LLM) as their default — the session must be routed through
    /// <c>/api/v1/proxy/s/{slug}/v1/</c> as an OpenAI-compatible provider
    /// regardless of any legacy <c>DefaultProvider</c> string, and any
    /// model hint shorter than a slash-separated name is treated as a
    /// user-selected model id from the probe list.
    /// </summary>
    public static AiSessionTarget ResolveSessionTargetForProxyService(
        string? modelOverride,
        string? defaultProxyModel,
        string? resumedModel)
    {
        var modelInput = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride.Trim()
            : !string.IsNullOrWhiteSpace(resumedModel)
                ? resumedModel
                : defaultProxyModel;

        return new AiSessionTarget(
            AiProvider.OpenAI,
            string.IsNullOrWhiteSpace(modelInput) ? "gpt-4o" : modelInput.Trim());
    }

    public static AiSessionTarget ResolveSessionTarget(
        string? providerFlag,
        string? modelOverride,
        string? resumedProvider,
        string? resumedModel)
    {
        var persistedProvider = TryParse(resumedProvider, out var parsedPersistedProvider)
            ? parsedPersistedProvider
            : (AiProvider?)null;

        var explicitProvider = TryParse(providerFlag, out var parsedExplicitProvider)
            ? parsedExplicitProvider
            : (AiProvider?)null;

        var providerChanged = explicitProvider.HasValue &&
                              persistedProvider.HasValue &&
                              explicitProvider.Value != persistedProvider.Value;

        var modelInput = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride.Trim()
            : providerChanged
                ? null
                : resumedModel;

        var provider = DetectProvider(providerFlag, modelInput, persistedProvider);
        var model = ResolveModel(modelInput, provider);
        return new AiSessionTarget(provider, model);
    }

    /// <summary>
    /// Resolves the full session plan — provider/model target plus any active
    /// NyxID AI-Service proxy routing — from the CLI flags, the stored NyxID
    /// defaults, and any resumed-session hints. An explicit <c>--provider</c> /
    /// <c>--model</c> flag always beats a stored proxy default; absent overrides,
    /// a stored proxy slug forces OpenAI-compatible routing through
    /// <c>/api/v1/proxy/s/{slug}/v1/</c>. Stored default models apply only to a
    /// fresh (non-resumed) session that did not pass an explicit model.
    /// </summary>
    public static AiSessionPlan PlanSession(
        string? cliProvider,
        string? cliModel,
        string? storedDefaultProvider,
        string? storedDefaultModel,
        string? storedDefaultProxySlug,
        string? storedDefaultProxyLabel,
        bool isResume,
        string? resumedProvider,
        string? resumedModel)
    {
        var defaultProviderFromStore = string.IsNullOrWhiteSpace(storedDefaultProvider)
            ? null
            : storedDefaultProvider;
        var defaultModelFromStore = !isResume &&
                                    string.IsNullOrWhiteSpace(cliModel) &&
                                    !string.IsNullOrWhiteSpace(storedDefaultModel)
            ? storedDefaultModel
            : null;

        var activeProxySlug = string.IsNullOrWhiteSpace(cliProvider) &&
                              !string.IsNullOrWhiteSpace(storedDefaultProxySlug)
            ? storedDefaultProxySlug
            : null;
        var activeProxyLabel = activeProxySlug != null
            ? storedDefaultProxyLabel
            : null;

        var target = activeProxySlug != null
            ? ResolveSessionTargetForProxyService(cliModel, defaultModelFromStore, resumedModel)
            : ResolveSessionTarget(
                cliProvider ?? defaultProviderFromStore,
                cliModel ?? defaultModelFromStore,
                resumedProvider,
                resumedModel);

        return new AiSessionPlan(target, activeProxySlug, activeProxyLabel);
    }

    public static AiProvider DetectProvider(
        string? providerHint,
        string? model,
        AiProvider? fallbackProvider = null)
    {
        if (TryParse(providerHint, out var explicitProvider))
            return explicitProvider;

        if (LooksLikeOpenAiModel(model))
            return AiProvider.OpenAI;

        if (LooksLikeOllamaModel(model))
            return AiProvider.Ollama;

        if (LooksLikeAnthropicModel(model))
            return AiProvider.Anthropic;

        return fallbackProvider ?? AiProvider.Anthropic;
    }

    public static string ResolveModel(string? input, AiProvider provider)
    {
        if (provider == AiProvider.OpenAI)
            return string.IsNullOrWhiteSpace(input) ? "gpt-4o" : input.Trim();

        if (provider == AiProvider.Ollama)
            return string.IsNullOrWhiteSpace(input) ? "qwen3:4b" : input.Trim();

        return ClaudeModels.Resolve(input);
    }

    public static bool TryParse(string? value, out AiProvider provider)
    {
        if (string.Equals(value, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Ollama;
            return true;
        }

        if (string.Equals(value, "openai", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.OpenAI;
            return true;
        }

        if (string.Equals(value, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Anthropic;
            return true;
        }

        provider = default;
        return false;
    }

    public static string ToStorageValue(AiProvider provider) =>
        provider switch
        {
            AiProvider.OpenAI => "openai",
            AiProvider.Ollama => "ollama",
            _ => "anthropic",
        };

    public static bool LooksLikeOpenAiModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var trimmed = model.Trim();
        return trimmed.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeAnthropicModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        ClaudeModelCatalog.TryResolve(model.Trim()) != null;

    public static bool LooksLikeOllamaModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var trimmed = model.Trim();
        return trimmed.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains(':', StringComparison.Ordinal);
    }
}
