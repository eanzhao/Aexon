using Aexon.Core.Auth;

namespace Aexon.Cli;

internal sealed record NyxIdCliSettings(
    string DefaultBaseUrl,
    string ActiveBaseUrl,
    bool HasStoredCredentials)
{
    public const string HostedBaseUrl = "https://nyx-api.chrono-ai.fun";
}

internal static class NyxIdCliSettingsLoader
{
    public static NyxIdCliSettings Load(
        NyxIdCredentialStore credentialStore,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);

        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var configuredBaseUrl = Normalize(getEnvironmentVariable("NYXID_BASE_URL")) ??
                                NyxIdCliSettings.HostedBaseUrl;
        var stored = credentialStore.Load();
        return new NyxIdCliSettings(
            configuredBaseUrl,
            stored?.BaseUrl ?? configuredBaseUrl,
            stored != null);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.ToString().TrimEnd('/')
            : null;
    }
}
