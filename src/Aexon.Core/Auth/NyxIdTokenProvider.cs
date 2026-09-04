namespace Aexon.Core.Auth;

/// <summary>
/// Returns a valid NyxID access token, refreshing stored credentials when needed.
/// </summary>
public sealed class NyxIdTokenProvider
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    private readonly NyxIdCredentialStore _credentialStore;
    private readonly NyxIdAuthService _authService;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public NyxIdTokenProvider(
        NyxIdCredentialStore credentialStore,
        NyxIdAuthService authService,
        Func<DateTimeOffset>? clock = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var credentials = _credentialStore.Load()
                          ?? throw new NotLoggedInException("NyxID login required. Run /login first.");

        if (!NeedsRefresh(credentials) &&
            !string.IsNullOrWhiteSpace(credentials.AccessToken))
        {
            return credentials.AccessToken;
        }

        var refreshed = await RefreshCoreAsync(force: false, cancellationToken);
        return refreshed.AccessToken;
    }

    public async Task<string> ForceRefreshAsync(CancellationToken cancellationToken = default)
    {
        var refreshed = await RefreshCoreAsync(force: true, cancellationToken);
        return refreshed.AccessToken;
    }

    private async Task<NyxIdCredentials> RefreshCoreAsync(bool force, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var current = _credentialStore.Load()
                          ?? throw new NotLoggedInException("NyxID login required. Run /login first.");

            if (!force &&
                !NeedsRefresh(current) &&
                !string.IsNullOrWhiteSpace(current.AccessToken))
            {
                return current;
            }

            if (string.IsNullOrWhiteSpace(current.RefreshToken))
            {
                _credentialStore.Clear();
                throw new NotLoggedInException("NyxID session expired. Run /login again.");
            }

            try
            {
                var refreshed = await _authService.RefreshAsync(
                    current.BaseUrl,
                    current.RefreshToken,
                    cancellationToken);
                var merged = current with
                {
                    AccessToken = refreshed.AccessToken,
                    RefreshToken = refreshed.RefreshToken ?? current.RefreshToken,
                    IdToken = refreshed.IdToken ?? current.IdToken,
                    ExpiresAt = refreshed.ExpiresAt,
                    ClientId = string.IsNullOrWhiteSpace(refreshed.ClientId)
                        ? current.ClientId
                        : refreshed.ClientId,
                };
                _credentialStore.Save(merged);
                return merged;
            }
            catch (NyxIdAuthService.NyxIdProtocolException ex) when (
                ex.ErrorCode is "invalid_grant" or "invalid_token" ||
                ex.StatusCode is System.Net.HttpStatusCode.BadRequest or
                    System.Net.HttpStatusCode.Unauthorized)
            {
                _credentialStore.Clear();
                throw new NotLoggedInException("NyxID session expired. Run /login again.", ex);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool NeedsRefresh(NyxIdCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.AccessToken))
            return true;

        return credentials.ExpiresAt <= _clock().Add(RefreshSkew);
    }
}
