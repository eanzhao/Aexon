using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aexon.Core.Aevatar;

/// <summary>
/// Per-machine aevatar backend configuration: which aevatar app/API to chat with,
/// which scope, and the last conversation actor id for implicit turn continuation.
/// </summary>
public sealed record AevatarChatSettings
{
    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("scope_id")]
    public string? ScopeId { get; init; }

    [JsonPropertyName("last_actor_id")]
    public string? LastActorId { get; init; }
}

/// <summary>
/// Persists <see cref="AevatarChatSettings"/> under <c>~/.aexon/aevatar.json</c>.
/// Separate file from nyxid.json so the NyxID login never collides with
/// per-backend chat preferences.
/// </summary>
public sealed class AevatarChatSettingsStore
{
    public const string DefaultScopeId = "default";

    /// <summary>
    /// Aevatar mainnet console backend — the out-of-box default so <c>aexon aevatar "hi"</c>
    /// works without any prior <c>config set-url</c>.
    /// </summary>
    public const string MainnetBaseUrl = "https://aevatar-console-backend-api.aevatar.ai";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public AevatarChatSettingsStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aexon",
                "aevatar.json")
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public AevatarChatSettings Load()
    {
        if (!File.Exists(FilePath))
            return new AevatarChatSettings();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AevatarChatSettings>(json, SerializerOptions)
                   ?? new AevatarChatSettings();
        }
        catch (IOException)
        {
            return new AevatarChatSettings();
        }
        catch (JsonException)
        {
            return new AevatarChatSettings();
        }
    }

    public void Save(AevatarChatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(FilePath)
                        ?? throw new InvalidOperationException("Aevatar settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var normalized = settings with
        {
            BaseUrl = NormalizeBaseUrl(settings.BaseUrl),
            ScopeId = string.IsNullOrWhiteSpace(settings.ScopeId) ? null : settings.ScopeId.Trim(),
            LastActorId = string.IsNullOrWhiteSpace(settings.LastActorId) ? null : settings.LastActorId.Trim(),
        };

        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(normalized, SerializerOptions);
        File.WriteAllText(tempPath, json);
        ApplyUnixPermissions(tempPath);
        File.Move(tempPath, FilePath, overwrite: true);
        ApplyUnixPermissions(FilePath);
    }

    /// <summary>
    /// Returns the effective scope id: explicit override > saved > "default".
    /// </summary>
    public static string ResolveScopeId(AevatarChatSettings settings, string? @override) =>
        string.IsNullOrWhiteSpace(@override)
            ? (string.IsNullOrWhiteSpace(settings.ScopeId) ? DefaultScopeId : settings.ScopeId!.Trim())
            : @override.Trim();

    /// <summary>
    /// Returns the effective base URL — explicit override > saved > aevatar mainnet default.
    /// </summary>
    public static string ResolveBaseUrl(AevatarChatSettings settings, string? @override)
    {
        var candidate = string.IsNullOrWhiteSpace(@override) ? settings.BaseUrl : @override;
        return NormalizeBaseUrl(candidate) ?? MainnetBaseUrl;
    }

    /// <summary>
    /// Validates and normalizes an aevatar base URL for both persisted settings
    /// and per-invocation overrides.
    /// </summary>
    public static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.ToString().TrimEnd('/')
            : null;
    }

    private static void ApplyUnixPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
