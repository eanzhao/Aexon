using System.ClientModel;
using System.ClientModel.Primitives;
using Aexon.Core.Auth;
using Aexon.Core.Configuration;
using Aexon.Core.Query;
using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace Aexon.Cli;

internal sealed record ChatClientBootstrap(
    IChatClient ChatClient,
    bool HasRequiredConfiguration,
    string? StartupSummary);

internal static class ChatClientFactory
{
    public static ChatClientBootstrap Create(
        AiProvider provider,
        string model,
        AnthropicClientSettings anthropicSettings,
        NyxIdRoutingContext? nyxIdRouting = null)
    {
        return provider switch
        {
            AiProvider.OpenAI => nyxIdRouting != null
                ? CreateOpenAIWithNyxId(model, nyxIdRouting)
                : CreateOpenAI(model),
            AiProvider.Ollama => CreateOllama(model),
            _ => nyxIdRouting != null
                ? CreateAnthropicWithNyxId(model, nyxIdRouting)
                : CreateAnthropic(model, anthropicSettings),
        };
    }

    private static ChatClientBootstrap CreateAnthropic(
        string model,
        AnthropicClientSettings settings)
    {
        var responseObserver = new ApiResponseObserver();
        var client = CreateAnthropicClient(settings, responseObserver);
        var chatClient = new OwnedChatClient(
            new AnthropicRetryMiddleware(
                new AnthropicThinkingMiddleware(
                    client.AsIChatClient(model, defaultMaxOutputTokens: 16384)),
                responseObserver),
            client);

        return new ChatClientBootstrap(
            chatClient,
            settings.HasApiKey,
            settings.StartupSummary);
    }

    private static ChatClientBootstrap CreateOpenAI(string model)
    {
        var settings = OpenAIClientSettingsLoader.Load();

        OpenAIClientOptions? clientOptions = null;
        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            clientOptions = new OpenAIClientOptions { Endpoint = new Uri(settings.BaseUrl) };

        var credential = new ApiKeyCredential(settings.ApiKey ?? "dummy-key");
        var client = clientOptions != null
            ? new OpenAIClient(credential, clientOptions)
            : new OpenAIClient(credential);

        return new ChatClientBootstrap(
            client.GetChatClient(model).AsIChatClient(),
            settings.HasUsableConfiguration,
            settings.StartupSummary);
    }

    private static ChatClientBootstrap CreateOpenAIWithNyxId(
        string model,
        NyxIdRoutingContext routing)
    {
        var proxyBaseUrl = NyxIdProxyEndpoints.Resolve(routing.BaseUrl, AiProvider.OpenAI);
        var httpClient = new HttpClient(
            new NyxIdProxyAuthenticationHandler(routing.TokenProvider)
            {
                InnerHandler = new HttpClientHandler(),
            },
            disposeHandler: true);
        var client = new OpenAIClient(
            new ApiKeyCredential("nyxid-proxy"),
            new OpenAIClientOptions
            {
                Endpoint = proxyBaseUrl,
                Transport = new HttpClientPipelineTransport(httpClient),
            });

        return new ChatClientBootstrap(
            new OwnedChatClient(client.GetChatClient(model).AsIChatClient(), httpClient),
            routing.HasStoredCredentials,
            BuildNyxIdStartupSummary("OpenAI", proxyBaseUrl, routing.HasStoredCredentials));
    }

    private static ChatClientBootstrap CreateOllama(string model)
    {
        var settings = OllamaClientSettingsLoader.Load();
        var client = new OllamaApiClient(new Uri(settings.BaseUrl), model);

        return new ChatClientBootstrap(
            (IChatClient)client,
            true,
            settings.StartupSummary);
    }

    private static ChatClientBootstrap CreateAnthropicWithNyxId(
        string model,
        NyxIdRoutingContext routing)
    {
        var responseObserver = new ApiResponseObserver();
        var proxyBaseUrl = NyxIdProxyEndpoints.Resolve(routing.BaseUrl, AiProvider.Anthropic);
        var client = new AnthropicClient
        {
            ApiKey = "nyxid-proxy",
            BaseUrl = proxyBaseUrl.ToString().TrimEnd('/'),
            HttpClient = responseObserver.CreateHttpClient(
                new NyxIdProxyAuthenticationHandler(routing.TokenProvider)
                {
                    InnerHandler = new HttpClientHandler(),
                }),
            MaxRetries = 0,
        };

        var chatClient = new OwnedChatClient(
            new AnthropicRetryMiddleware(
                new AnthropicThinkingMiddleware(
                    client.AsIChatClient(model, defaultMaxOutputTokens: 16384)),
                responseObserver),
            client);

        return new ChatClientBootstrap(
            chatClient,
            routing.HasStoredCredentials,
            BuildNyxIdStartupSummary("Anthropic", proxyBaseUrl, routing.HasStoredCredentials));
    }

    private static AnthropicClient CreateAnthropicClient(
        AnthropicClientSettings settings,
        ApiResponseObserver? responseObserver = null)
    {
        var httpClient = responseObserver?.CreateHttpClient();

        if (settings.HasApiKey && !string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return httpClient != null
                ? new AnthropicClient
                {
                    ApiKey = settings.ApiKey!,
                    BaseUrl = settings.BaseUrl,
                    HttpClient = httpClient,
                    MaxRetries = 0,
                }
                : new AnthropicClient
                {
                    ApiKey = settings.ApiKey!,
                    BaseUrl = settings.BaseUrl,
                    MaxRetries = 0,
                };
        }

        if (settings.HasApiKey)
        {
            return httpClient != null
                ? new AnthropicClient
                {
                    ApiKey = settings.ApiKey!,
                    HttpClient = httpClient,
                    MaxRetries = 0,
                }
                : new AnthropicClient
                {
                    ApiKey = settings.ApiKey!,
                    MaxRetries = 0,
                };
        }

        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return httpClient != null
                ? new AnthropicClient
                {
                    BaseUrl = settings.BaseUrl,
                    HttpClient = httpClient,
                    MaxRetries = 0,
                }
                : new AnthropicClient
                {
                    BaseUrl = settings.BaseUrl,
                    MaxRetries = 0,
                };
        }

        return httpClient != null
            ? new AnthropicClient
            {
                HttpClient = httpClient,
                MaxRetries = 0,
            }
            : new AnthropicClient
            {
                MaxRetries = 0,
            };
    }

    private sealed class OwnedChatClient(
        IChatClient innerClient,
        IDisposable? ownedResource = null) : DelegatingChatClient(innerClient)
    {
        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                    ownedResource?.Dispose();
            }
        }
    }

    private static string BuildNyxIdStartupSummary(
        string providerName,
        Uri proxyBaseUrl,
        bool hasStoredCredentials)
    {
        var summary = $"NyxID routing: proxying {providerName} via {proxyBaseUrl}.";
        if (hasStoredCredentials)
            return summary;

        return $"{summary}{Environment.NewLine}NyxID routing is enabled, but no NyxID credentials are stored yet. Run aexon /login first.";
    }
}

internal sealed record NyxIdRoutingContext(
    string BaseUrl,
    NyxIdTokenProvider TokenProvider,
    bool HasStoredCredentials);
