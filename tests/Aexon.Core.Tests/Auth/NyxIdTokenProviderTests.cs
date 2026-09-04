using System.Net;
using System.Text;
using Aexon.Core.Auth;
using Aexon.Core.Tests.Runtime;

namespace Aexon.Core.Tests.Auth;

public sealed class NyxIdTokenProviderTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-04-17T12:00:00Z");

    [Fact]
    public async Task GetValidAccessTokenAsync_RefreshesWhenTokenIsNearExpiry()
    {
        using var temp = new TempDirectory();
        var store = new NyxIdCredentialStore(temp.FullPath("nyxid.json"));
        store.Save(new NyxIdCredentials
        {
            BaseUrl = "https://nyx.example",
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            IdToken = "old-id",
            ExpiresAt = FixedNow.AddSeconds(30),
            ClientId = "client-123",
        });

        var handler = new RoutedHandler(
            discoveryResponse: """
                {
                  "authorization_endpoint": "https://nyx.example/oauth/authorize",
                  "token_endpoint": "https://nyx.example/oauth/token"
                }
                """,
            tokenResponse: """
                {
                  "access_token": "new-access",
                  "refresh_token": "new-refresh",
                  "id_token": "new-id",
                  "expires_in": 900
                }
                """);
        var authService = new NyxIdAuthService(new HttpClient(handler), store, () => FixedNow);
        var provider = new NyxIdTokenProvider(store, authService, () => FixedNow);

        var accessToken = await provider.GetValidAccessTokenAsync();

        Assert.Equal("new-access", accessToken);
        Assert.Equal(2, handler.Requests.Count);

        var refreshRequest = handler.Requests[1];
        var body = await refreshRequest.Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=old-refresh", body, StringComparison.Ordinal);
        Assert.Contains("client_id=client-123", body, StringComparison.Ordinal);

        var persisted = store.Load()!;
        Assert.Equal("new-access", persisted.AccessToken);
        Assert.Equal("new-refresh", persisted.RefreshToken);
        Assert.Equal("new-id", persisted.IdToken);
        Assert.Equal(FixedNow.AddSeconds(900), persisted.ExpiresAt);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_DoesNotRefreshWhenTokenIsStillFresh()
    {
        using var temp = new TempDirectory();
        var store = new NyxIdCredentialStore(temp.FullPath("nyxid.json"));
        store.Save(new NyxIdCredentials
        {
            BaseUrl = "https://nyx.example",
            AccessToken = "fresh-access",
            RefreshToken = "fresh-refresh",
            IdToken = "fresh-id",
            ExpiresAt = FixedNow.AddMinutes(5),
            ClientId = "client-123",
        });

        var handler = new CountingHandler();
        var authService = new NyxIdAuthService(new HttpClient(handler), store, () => FixedNow);
        var provider = new NyxIdTokenProvider(store, authService, () => FixedNow);

        var accessToken = await provider.GetValidAccessTokenAsync();

        Assert.Equal("fresh-access", accessToken);
        Assert.Equal(0, handler.CallCount);
    }

    private sealed class RoutedHandler(string discoveryResponse, string tokenResponse) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var content = request.RequestUri?.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => discoveryResponse,
                "/oauth/token" => tokenResponse,
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Refresh should not be called.");
        }
    }
}
