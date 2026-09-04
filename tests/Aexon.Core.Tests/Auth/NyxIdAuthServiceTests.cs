using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aexon.Core.Auth;
using Aexon.Core.Tests.Runtime;

namespace Aexon.Core.Tests.Auth;

public sealed class NyxIdAuthServiceTests
{
    [Fact]
    public void CreatePkceParameters_GeneratesS256Challenge()
    {
        var pkce = NyxIdAuthService.CreatePkceParameters();

        Assert.Matches("^[A-Za-z0-9_-]{43,}$", pkce.CodeVerifier);

        var expectedChallenge = Convert.ToBase64String(
                SHA256.HashData(Encoding.ASCII.GetBytes(pkce.CodeVerifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(expectedChallenge, pkce.CodeChallenge);
    }

    [Fact]
    public void ValidateAuthorizationResponse_RejectsMismatchedState()
    {
        var query = new NameValueCollection
        {
            ["code"] = "auth-code",
            ["state"] = "wrong-state",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => NyxIdAuthService.ValidateAuthorizationResponse(query, "expected-state"));

        Assert.Contains("state validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeCodeAsync_PostsExpectedFormPayload()
    {
        using var temp = new TempDirectory();
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token": "access-1",
                      "refresh_token": "refresh-1",
                      "id_token": "id-1",
                      "expires_in": 900
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        var service = new NyxIdAuthService(
            new HttpClient(handler),
            new NyxIdCredentialStore(temp.FullPath("nyxid.json")));

        var response = await service.ExchangeCodeAsync(
            new Uri("https://nyx.example/oauth/token"),
            "client-123",
            "auth-code",
            "http://127.0.0.1:7777/callback",
            "verifier-123");

        Assert.Equal("access-1", response.AccessToken);
        Assert.Equal("refresh-1", response.RefreshToken);
        Assert.Equal("id-1", response.IdToken);
        Assert.Equal(900, response.ExpiresIn);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://nyx.example/oauth/token", request.RequestUri?.ToString());

        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
        Assert.Contains("code=auth-code", body, StringComparison.Ordinal);
        Assert.Contains("client_id=client-123", body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=verifier-123", body, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A7777%2Fcallback", body, StringComparison.Ordinal);
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
}
