using Aexon.Core.Query;

namespace Aexon.Core.Tests.Runtime;

/// <summary>
/// Contains tests for AI provider/model selection.
/// </summary>
public sealed class AiProviderSelectionTests
{
    [Fact]
    public void ResolveSessionTarget_PrefersPersistedProviderForCustomOpenAiModel()
    {
        var target = AiProviderSelection.ResolveSessionTarget(
            providerFlag: null,
            modelOverride: null,
            resumedProvider: "openai",
            resumedModel: "qwen2.5-coder");

        Assert.Equal(AiProvider.OpenAI, target.Provider);
        Assert.Equal("qwen2.5-coder", target.Model);
    }

    [Fact]
    public void ResolveSessionTarget_UsesProviderDefaultWhenResumeProviderChangesWithoutModelOverride()
    {
        var target = AiProviderSelection.ResolveSessionTarget(
            providerFlag: "openai",
            modelOverride: null,
            resumedProvider: "anthropic",
            resumedModel: "claude-opus-4-6");

        Assert.Equal(AiProvider.OpenAI, target.Provider);
        Assert.Equal("gpt-4o", target.Model);
    }

    [Fact]
    public void ResolveSessionTarget_UsesOllamaDefaultWhenProviderIsExplicit()
    {
        var target = AiProviderSelection.ResolveSessionTarget(
            providerFlag: "ollama",
            modelOverride: null,
            resumedProvider: "anthropic",
            resumedModel: "claude-sonnet-4-6");

        Assert.Equal(AiProvider.Ollama, target.Provider);
        Assert.Equal("qwen3:4b", target.Model);
    }

    [Fact]
    public void DetectProvider_PrefersExplicitAnthropicAliasesOverOpenAiFallback()
    {
        var provider = AiProviderSelection.DetectProvider(
            providerHint: null,
            model: "opus",
            fallbackProvider: AiProvider.OpenAI);

        Assert.Equal(AiProvider.Anthropic, provider);
    }

    [Fact]
    public void TryParse_RecognizesOllamaProvider()
    {
        var success = AiProviderSelection.TryParse("ollama", out var provider);

        Assert.True(success);
        Assert.Equal(AiProvider.Ollama, provider);
        Assert.Equal("ollama", AiProviderSelection.ToStorageValue(provider));
    }

    [Fact]
    public void PlanSession_RoutesThroughProxyServiceWhenStoredSlugAndNoProviderFlag()
    {
        var plan = AiProviderSelection.PlanSession(
            cliProvider: null,
            cliModel: null,
            storedDefaultProvider: "anthropic",
            storedDefaultModel: null,
            storedDefaultProxySlug: "chrono",
            storedDefaultProxyLabel: "Chrono LLM",
            isResume: false,
            resumedProvider: null,
            resumedModel: null);

        Assert.Equal("chrono", plan.ProxyServiceSlug);
        Assert.Equal("Chrono LLM", plan.ProxyServiceLabel);
        Assert.Equal(AiProvider.OpenAI, plan.Target.Provider);
        Assert.Equal("gpt-4o", plan.Target.Model);
    }

    [Fact]
    public void PlanSession_ExplicitProviderFlagBeatsStoredProxyDefault()
    {
        var plan = AiProviderSelection.PlanSession(
            cliProvider: "anthropic",
            cliModel: "opus",
            storedDefaultProvider: null,
            storedDefaultModel: null,
            storedDefaultProxySlug: "chrono",
            storedDefaultProxyLabel: "Chrono LLM",
            isResume: false,
            resumedProvider: null,
            resumedModel: null);

        Assert.Null(plan.ProxyServiceSlug);
        Assert.Null(plan.ProxyServiceLabel);
        Assert.Equal(AiProvider.Anthropic, plan.Target.Provider);
    }

    [Fact]
    public void PlanSession_UsesStoredDefaultModelForFreshSession()
    {
        var plan = AiProviderSelection.PlanSession(
            cliProvider: "openai",
            cliModel: null,
            storedDefaultProvider: "openai",
            storedDefaultModel: "gpt-4o-mini",
            storedDefaultProxySlug: null,
            storedDefaultProxyLabel: null,
            isResume: false,
            resumedProvider: null,
            resumedModel: null);

        Assert.Null(plan.ProxyServiceSlug);
        Assert.Equal(AiProvider.OpenAI, plan.Target.Provider);
        Assert.Equal("gpt-4o-mini", plan.Target.Model);
    }

    [Fact]
    public void PlanSession_IgnoresStoredDefaultModelWhenResuming()
    {
        var plan = AiProviderSelection.PlanSession(
            cliProvider: null,
            cliModel: null,
            storedDefaultProvider: "openai",
            storedDefaultModel: "gpt-4o-mini",
            storedDefaultProxySlug: null,
            storedDefaultProxyLabel: null,
            isResume: true,
            resumedProvider: "openai",
            resumedModel: "qwen2.5-coder");

        Assert.Null(plan.ProxyServiceSlug);
        Assert.Equal(AiProvider.OpenAI, plan.Target.Provider);
        Assert.Equal("qwen2.5-coder", plan.Target.Model);
    }
}
