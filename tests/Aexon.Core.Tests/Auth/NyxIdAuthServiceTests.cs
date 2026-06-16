using System.Net;
using System.Text;
using System.Text.Json;
using Aexon.Core.Auth;
using Aexon.Core.Tests.Runtime;

namespace Aexon.Core.Tests.Auth;

public sealed class NyxIdAuthServiceTests
{
    [Fact]
    public void BuildCliAuthUrl_IncludesPortStateAndClientUa()
    {
        var url = NyxIdAuthService.BuildCliAuthUrl("https://app.example.com/", 43123, "deadbeef");

        var parsed = new Uri(url);
        Assert.Equal("/cli-auth", parsed.AbsolutePath);

        var parameters = System.Web.HttpUtility.ParseQueryString(parsed.Query);
        Assert.Equal("43123", parameters["port"]);
        Assert.Equal("deadbeef", parameters["state"]);
        Assert.Equal(NyxIdAuthService.ClientUserAgent, parameters["client_ua"]);
    }

    [Fact]
    public void ParseCallback_ParsesValidRequest()
    {
        var request = "GET /callback?access_token=tok_abc&state=deadbeef HTTP/1.1\r\nHost: 127.0.0.1\r\n";

        var tokens = NyxIdAuthService.ParseCallback(request, "deadbeef");

        Assert.NotNull(tokens);
        Assert.Equal("tok_abc", tokens!.AccessToken);
        Assert.Null(tokens.RefreshToken);
    }

    [Fact]
    public void ParseCallback_ParsesRefreshToken()
    {
        var request = "GET /callback?access_token=tok_abc&refresh_token=ref_xyz&state=deadbeef HTTP/1.1\r\n";

        var tokens = NyxIdAuthService.ParseCallback(request, "deadbeef");

        Assert.NotNull(tokens);
        Assert.Equal("tok_abc", tokens!.AccessToken);
        Assert.Equal("ref_xyz", tokens.RefreshToken);
    }

    [Fact]
    public void ParseCallback_RejectsWrongState()
    {
        var request = "GET /callback?access_token=tok_abc&state=wrong HTTP/1.1\r\n";

        Assert.Null(NyxIdAuthService.ParseCallback(request, "deadbeef"));
    }

    [Fact]
    public void ParseCallback_RejectsNonCallbackPath()
    {
        var request = "GET /other?access_token=tok_abc&state=deadbeef HTTP/1.1\r\n";

        Assert.Null(NyxIdAuthService.ParseCallback(request, "deadbeef"));
    }

    [Fact]
    public void TryReadJwtExpiry_ReturnsExpFromPayload()
    {
        var expSeconds = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var jwt = BuildJwt($$"""{"sub":"u_1","exp":{{expSeconds}}}""");

        Assert.True(NyxIdAuthService.TryReadJwtExpiry(jwt, out var expiresAt));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expSeconds), expiresAt);
    }

    [Fact]
    public void TryReadJwtExpiry_ReturnsFalseWhenClaimMissing()
    {
        var jwt = BuildJwt("""{"sub":"u_1"}""");

        Assert.False(NyxIdAuthService.TryReadJwtExpiry(jwt, out _));
    }

    [Fact]
    public async Task RefreshAsync_PostsToAuthRefreshEndpoint()
    {
        using var temp = new TempDirectory();
        var expSeconds = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var jwt = BuildJwt($$"""{"exp":{{expSeconds}}}""");

        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    { "access_token": "{{jwt}}", "refresh_token": "new-refresh" }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        var service = new NyxIdAuthService(
            new HttpClient(handler),
            new NyxIdCredentialStore(temp.FullPath("nyxid.json"), temp.FullPath("preferences.json")));

        var credentials = await service.RefreshAsync("https://nyx.example/", "old-refresh");

        Assert.Equal(jwt, credentials.AccessToken);
        Assert.Equal("new-refresh", credentials.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expSeconds), credentials.ExpiresAt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://nyx.example/api/v1/auth/refresh", request.RequestUri?.ToString());

        var body = await request.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("old-refresh", doc.RootElement.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public async Task RefreshAsync_ReusesIncomingRefreshTokenWhenResponseOmitsIt()
    {
        using var temp = new TempDirectory();
        var expSeconds = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var jwt = BuildJwt($$"""{"exp":{{expSeconds}}}""");

        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    { "access_token": "{{jwt}}" }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        var service = new NyxIdAuthService(
            new HttpClient(handler),
            new NyxIdCredentialStore(temp.FullPath("nyxid.json"), temp.FullPath("preferences.json")));

        var credentials = await service.RefreshAsync("https://nyx.example", "keep-me");

        Assert.Equal("keep-me", credentials.RefreshToken);
    }

    [Fact]
    public async Task LogoutAsync_PostsBearerTokenAndClearsLocalCredentials()
    {
        using var temp = new TempDirectory();
        var store = new NyxIdCredentialStore(temp.FullPath("nyxid.json"), temp.FullPath("preferences.json"));
        store.Save(new NyxIdCredentials
        {
            BaseUrl = "https://nyx.example",
            AccessToken = "acc",
            RefreshToken = "ref",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            ClientId = NyxIdAuthService.SyntheticClientId,
        });

        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var service = new NyxIdAuthService(new HttpClient(handler), store);

        await service.LogoutAsync("https://nyx.example/", "acc");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://nyx.example/api/v1/auth/logout", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("acc", request.Headers.Authorization?.Parameter);

        Assert.Null(store.Load());
    }

    private static string BuildJwt(string payloadJson)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none"}"""));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        return $"{header}.{payload}.";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
}
