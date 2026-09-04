using System.Text.Json.Serialization;

namespace Aexon.Core.Auth;

/// <summary>
/// Stores NyxID tokens and client metadata for the active login.
/// </summary>
public sealed record NyxIdCredentials
{
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("client_id")]
    public string ClientId { get; init; } = string.Empty;
}
