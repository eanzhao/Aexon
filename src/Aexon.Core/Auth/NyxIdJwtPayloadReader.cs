using System.Text;
using System.Text.Json;

namespace Aexon.Core.Auth;

/// <summary>
/// Reads unverified JWT payload claims for local UX purposes.
/// </summary>
public static class NyxIdJwtPayloadReader
{
    public static bool TryGetStringClaim(
        string? jwt,
        string claimName,
        out string? value)
    {
        value = null;
        if (!TryParsePayload(jwt, out var payload))
            return false;

        using var document = payload;
        if (document == null)
            return false;

        if (!document.RootElement.TryGetProperty(claimName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    internal static bool TryParsePayload(string? jwt, out JsonDocument? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(jwt))
            return false;

        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return false;

        try
        {
            var bytes = DecodeBase64Url(parts[1]);
            payload = JsonDocument.Parse(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string input)
    {
        var normalized = input.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');

        return Convert.FromBase64String(normalized);
    }
}
