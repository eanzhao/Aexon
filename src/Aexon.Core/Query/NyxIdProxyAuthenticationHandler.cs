using System.Net;
using System.Net.Http.Headers;
using Aexon.Core.Auth;

namespace Aexon.Core.Query;

/// <summary>
/// Injects NyxID bearer tokens into proxied LLM requests and retries once on 401.
/// </summary>
public sealed class NyxIdProxyAuthenticationHandler : DelegatingHandler
{
    private readonly NyxIdTokenProvider _tokenProvider;

    public NyxIdProxyAuthenticationHandler(NyxIdTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var retryRequest = await CloneRequestAsync(request, cancellationToken);
        var accessToken = await _tokenProvider.GetValidAccessTokenAsync(cancellationToken);
        ApplyBearerToken(request, accessToken);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();

        var refreshedToken = await _tokenProvider.ForceRefreshAsync(cancellationToken);
        ApplyBearerToken(retryRequest, refreshedToken);

        var retryResponse = await base.SendAsync(retryRequest, cancellationToken);
        if (retryResponse.StatusCode != HttpStatusCode.Unauthorized)
            return retryResponse;

        retryResponse.Dispose();
        throw new NotLoggedInException("NyxID session is no longer valid. Run /login again.");
    }

    private static void ApplyBearerToken(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Remove("x-api-key");
        request.Headers.Remove("api-key");
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content != null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentClone = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                contentClone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            clone.Content = contentClone;
        }

        return clone;
    }
}
