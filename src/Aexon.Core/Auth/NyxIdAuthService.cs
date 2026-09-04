using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aexon.Core.Auth;

/// <summary>
/// Handles NyxID OIDC login, refresh, and logout flows.
/// </summary>
public sealed class NyxIdAuthService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly NyxIdCredentialStore _credentialStore;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Uri> _browserLauncher;

    public NyxIdAuthService(
        HttpClient? httpClient = null,
        NyxIdCredentialStore? credentialStore = null,
        Func<DateTimeOffset>? clock = null,
        Action<Uri>? browserLauncher = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _credentialStore = credentialStore ?? new NyxIdCredentialStore();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _browserLauncher = browserLauncher ?? OpenBrowser;
    }

    public async Task<NyxIdCredentials> LoginAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var discovery = await DiscoverAsync(normalizedBaseUrl, cancellationToken);
        var existing = _credentialStore.Load();
        var clientId = existing is { BaseUrl: var storedBaseUrl, ClientId: var storedClientId } &&
                       string.Equals(storedBaseUrl, normalizedBaseUrl, StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(storedClientId)
            ? storedClientId
            : await RegisterClientAsync(normalizedBaseUrl, cancellationToken);

        var pkce = CreatePkceParameters();
        var state = CreateRandomUrlSafeString(32);
        var nonce = CreateRandomUrlSafeString(32);
        var port = AllocateLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        using var listener = CreateLoopbackListener(port);

        var authorizationUri = BuildAuthorizationUri(
            discovery.AuthorizationEndpoint,
            clientId,
            redirectUri,
            pkce.CodeChallenge,
            state,
            nonce);

        _browserLauncher(authorizationUri);

        var code = await WaitForAuthorizationCodeAsync(listener, state, cancellationToken);
        var exchanged = await ExchangeCodeAsync(
            discovery.TokenEndpoint,
            clientId,
            code,
            redirectUri,
            pkce.CodeVerifier,
            cancellationToken);

        ValidateNonce(exchanged.IdToken, nonce);

        return new NyxIdCredentials
        {
            BaseUrl = normalizedBaseUrl,
            ClientId = clientId,
            AccessToken = exchanged.AccessToken,
            RefreshToken = exchanged.RefreshToken,
            IdToken = exchanged.IdToken,
            ExpiresAt = _clock().AddSeconds(exchanged.ExpiresIn),
        };
    }

    public async Task<NyxIdCredentials> RefreshAsync(
        string baseUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var discovery = await DiscoverAsync(normalizedBaseUrl, cancellationToken);
        var existing = _credentialStore.Load();
        var clientId = existing is { BaseUrl: var storedBaseUrl, ClientId: var storedClientId } &&
                       string.Equals(storedBaseUrl, normalizedBaseUrl, StringComparison.OrdinalIgnoreCase)
            ? storedClientId
            : string.Empty;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        if (!string.IsNullOrWhiteSpace(clientId))
            form["client_id"] = clientId;

        var tokenResponse = await RequestTokenAsync(discovery.TokenEndpoint, form, cancellationToken);
        return new NyxIdCredentials
        {
            BaseUrl = normalizedBaseUrl,
            ClientId = clientId,
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken ?? refreshToken,
            IdToken = tokenResponse.IdToken ?? existing?.IdToken,
            ExpiresAt = _clock().AddSeconds(tokenResponse.ExpiresIn),
        };
    }

    public async Task LogoutAsync(
        string baseUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);

        try
        {
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                using var response = await _httpClient.PostAsync(
                    $"{normalizedBaseUrl}/oauth/revoke",
                    new FormUrlEncodedContent(
                    [
                        new KeyValuePair<string, string>("token", refreshToken),
                    ]),
                    cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
            }
        }
        finally
        {
            _credentialStore.Clear();
        }
    }

    internal async Task<NyxIdTokenResponse> ExchangeCodeAsync(
        Uri tokenEndpoint,
        string clientId,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
        };

        return await RequestTokenAsync(tokenEndpoint, form, cancellationToken);
    }

    internal static PkceParameters CreatePkceParameters()
    {
        var verifier = CreateRandomUrlSafeString(32);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(challengeBytes);
        return new PkceParameters(verifier, challenge);
    }

    internal static string ValidateAuthorizationResponse(
        NameValueCollection query,
        string expectedState)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);

        var error = query["error"];
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"NyxID login failed: {error}.");

        var state = query["state"];
        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            throw new InvalidOperationException("NyxID login failed: state validation failed.");

        var code = query["code"];
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("NyxID login failed: authorization code missing from callback.");

        return code;
    }

    private async Task<NyxIdDiscoveryDocument> DiscoverAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"{baseUrl}/.well-known/openid-configuration",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var discovery = await JsonSerializer.DeserializeAsync<NyxIdDiscoveryDocument>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (discovery == null ||
            discovery.AuthorizationEndpoint == null ||
            discovery.TokenEndpoint == null)
        {
            throw new InvalidOperationException("NyxID discovery response is missing required endpoints.");
        }

        return discovery;
    }

    private async Task<string> RegisterClientAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var request = new NyxIdRegistrationRequest
        {
            ClientName = "Aexon",
            RedirectUris = ["http://127.0.0.1:0/callback"],
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"{baseUrl}/oauth/register",
            request,
            SerializerOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var registration = await JsonSerializer.DeserializeAsync<NyxIdRegistrationResponse>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (registration == null || string.IsNullOrWhiteSpace(registration.ClientId))
            throw new InvalidOperationException("NyxID client registration did not return a client_id.");

        return registration.ClientId;
    }

    private async Task<string> WaitForAuthorizationCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        try
        {
            if (!string.Equals(context.Request.Url?.AbsolutePath, "/callback", StringComparison.Ordinal))
                throw new InvalidOperationException("NyxID login failed: unexpected callback path.");

            var code = ValidateAuthorizationResponse(context.Request.QueryString, expectedState);
            await WriteCallbackResponseAsync(context.Response, HttpStatusCode.OK, "NyxID login complete. You can return to Aexon.");
            return code;
        }
        catch
        {
            await WriteCallbackResponseAsync(context.Response, HttpStatusCode.BadRequest, "NyxID login failed. Return to Aexon for details.");
            throw;
        }
    }

    private static async Task WriteCallbackResponseAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        string message)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/html; charset=utf-8";
        await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: false);
        await writer.WriteAsync(
            $"""
            <html>
              <body>
                <p>{WebUtility.HtmlEncode(message)}</p>
              </body>
            </html>
            """);
    }

    private async Task<NyxIdTokenResponse> RequestTokenAsync(
        Uri tokenEndpoint,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(formValues),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var tokenResponse = await JsonSerializer.DeserializeAsync<NyxIdTokenResponse>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (tokenResponse == null ||
            string.IsNullOrWhiteSpace(tokenResponse.AccessToken) ||
            tokenResponse.ExpiresIn <= 0)
        {
            throw new InvalidOperationException("NyxID token response is missing required fields.");
        }

        if (string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
            throw new InvalidOperationException("NyxID token response did not include a refresh token.");

        return tokenResponse;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? errorCode = null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<NyxIdErrorResponse>(
                stream,
                SerializerOptions,
                cancellationToken);
            errorCode = payload?.Error;
        }
        catch (JsonException)
        {
            // Ignore malformed error payloads.
        }

        throw new NyxIdProtocolException(
            response.StatusCode,
            errorCode,
            $"NyxID request failed with status {(int)response.StatusCode}.");
    }

    private static void ValidateNonce(string? idToken, string expectedNonce)
    {
        if (!NyxIdJwtPayloadReader.TryGetStringClaim(idToken, "nonce", out var nonce))
            throw new InvalidOperationException("NyxID login failed: ID token did not include a nonce.");

        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new InvalidOperationException("NyxID login failed: ID token nonce mismatch.");
    }

    private static HttpListener CreateLoopbackListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static int AllocateLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Uri BuildAuthorizationUri(
        Uri authorizationEndpoint,
        string clientId,
        string redirectUri,
        string codeChallenge,
        string state,
        string nonce)
    {
        var builder = new UriBuilder(authorizationEndpoint);
        builder.Query = string.Join(
            "&",
            new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = "openid profile email",
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["nonce"] = nonce,
            }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return builder.Uri;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("NyxID base URL must be an absolute URL.", nameof(baseUrl));

        return uri.ToString().TrimEnd('/');
    }

    private static Uri EnsureAbsoluteUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("NyxID discovery endpoint must be an absolute URL.");

        return uri;
    }

    private static string CreateRandomUrlSafeString(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void OpenBrowser(Uri uri)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });

        if (process == null)
            throw new InvalidOperationException($"Failed to open the browser. Open this URL manually: {uri}");
    }

    internal sealed record PkceParameters(string CodeVerifier, string CodeChallenge);

    private sealed record NyxIdDiscoveryDocument
    {
        [JsonPropertyName("authorization_endpoint")]
        public required Uri AuthorizationEndpoint { get; init; }

        [JsonPropertyName("token_endpoint")]
        public required Uri TokenEndpoint { get; init; }
    }

    private sealed record NyxIdRegistrationRequest
    {
        [JsonPropertyName("client_name")]
        public required string ClientName { get; init; }

        [JsonPropertyName("redirect_uris")]
        public required string[] RedirectUris { get; init; }

        [JsonPropertyName("grant_types")]
        public required string[] GrantTypes { get; init; }

        [JsonPropertyName("response_types")]
        public required string[] ResponseTypes { get; init; }

        [JsonPropertyName("token_endpoint_auth_method")]
        public required string TokenEndpointAuthMethod { get; init; }
    }

    private sealed record NyxIdRegistrationResponse
    {
        [JsonPropertyName("client_id")]
        public required string ClientId { get; init; }
    }

    internal sealed record NyxIdTokenResponse
    {
        [JsonPropertyName("access_token")]
        public required string AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; init; }

        [JsonPropertyName("expires_in")]
        public required int ExpiresIn { get; init; }
    }

    private sealed record NyxIdErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }

    internal sealed class NyxIdProtocolException : InvalidOperationException
    {
        public NyxIdProtocolException(HttpStatusCode statusCode, string? errorCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }

        public HttpStatusCode StatusCode { get; }

        public string? ErrorCode { get; }
    }
}
