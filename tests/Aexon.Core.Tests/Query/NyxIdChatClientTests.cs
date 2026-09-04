using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Aexon.Core.Auth;
using Aexon.Core.Query;
using Aexon.Core.Tests.Runtime;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Aexon.Core.Tests.Query;

public sealed class NyxIdChatClientTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-04-17T12:00:00Z");

    [Fact]
    public async Task OpenAiRouting_UsesProxyBaseUrlAndBearerHeader()
    {
        using var temp = new TempDirectory();
        var tokenProvider = CreateTokenProvider(temp, "openai-access");
        var handler = new RecordingHandler(CreateOpenAiResponse());
        using var httpClient = new HttpClient(
            new NyxIdProxyAuthenticationHandler(tokenProvider)
            {
                InnerHandler = handler,
            },
            disposeHandler: true);
        var client = new OpenAIClient(
            new ApiKeyCredential("placeholder"),
            new OpenAIClientOptions
            {
                Endpoint = NyxIdProxyEndpoints.Resolve("https://nyx.example", AiProvider.OpenAI),
                Transport = new HttpClientPipelineTransport(httpClient),
            });

        var chatClient = client.GetChatClient("gpt-4o").AsIChatClient();
        await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith(
            "https://nyx.example/api/v1/proxy/s/llm-openai/",
            request.RequestUri?.ToString(),
            StringComparison.Ordinal);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("openai-access", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task AnthropicRouting_UsesProxyBaseUrlAndBearerHeader()
    {
        using var temp = new TempDirectory();
        var tokenProvider = CreateTokenProvider(temp, "anthropic-access");
        var handler = new RecordingHandler(CreateAnthropicResponse());
        using var httpClient = new HttpClient(
            new NyxIdProxyAuthenticationHandler(tokenProvider)
            {
                InnerHandler = handler,
            },
            disposeHandler: true);
        var client = new AnthropicClient
        {
            ApiKey = "placeholder",
            BaseUrl = NyxIdProxyEndpoints.Resolve("https://nyx.example", AiProvider.Anthropic).ToString().TrimEnd('/'),
            HttpClient = httpClient,
            MaxRetries = 0,
        };

        var chatClient = client.AsIChatClient("claude-sonnet-4-6");
        await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith(
            "https://nyx.example/api/v1/proxy/s/llm-anthropic/",
            request.RequestUri?.ToString(),
            StringComparison.Ordinal);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("anthropic-access", request.Headers.Authorization?.Parameter);
        Assert.False(request.Headers.Contains("x-api-key"));
    }

    private static NyxIdTokenProvider CreateTokenProvider(TempDirectory temp, string accessToken)
    {
        var store = new NyxIdCredentialStore(temp.FullPath($"{accessToken}.json"));
        store.Save(new NyxIdCredentials
        {
            BaseUrl = "https://nyx.example",
            AccessToken = accessToken,
            RefreshToken = "refresh-token",
            IdToken = "id-token",
            ExpiresAt = FixedNow.AddMinutes(10),
            ClientId = "client-123",
        });

        var authService = new NyxIdAuthService(
            new HttpClient(new UnexpectedRequestHandler()),
            store,
            () => FixedNow);
        return new NyxIdTokenProvider(store, authService, () => FixedNow);
    }

    private static HttpResponseMessage CreateOpenAiResponse()
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-1",
            @object = "chat.completion",
            created = 1_700_000_000,
            model = "gpt-4o",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = "ok",
                    },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = 1,
                completion_tokens = 1,
                total_tokens = 2,
            },
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage CreateAnthropicResponse()
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = "msg-1",
            type = "message",
            role = "assistant",
            model = "claude-sonnet-4-6",
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            content = new[]
            {
                new
                {
                    type = "text",
                    text = "ok",
                },
            },
            usage = new
            {
                input_tokens = 1,
                output_tokens = 1,
                cache_read_input_tokens = 0,
                cache_creation_input_tokens = 0,
            },
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Token refresh should not be called.");
    }
}
