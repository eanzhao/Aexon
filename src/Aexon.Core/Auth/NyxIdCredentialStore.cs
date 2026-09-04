using System.Text.Json;

namespace Aexon.Core.Auth;

/// <summary>
/// Persists NyxID credentials to the local filesystem.
/// </summary>
public sealed class NyxIdCredentialStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public NyxIdCredentialStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aexon",
                "nyxid.json")
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public NyxIdCredentials? Load()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            var json = File.ReadAllText(FilePath);
            var credentials = JsonSerializer.Deserialize<NyxIdCredentials>(json, SerializerOptions);
            if (credentials == null ||
                string.IsNullOrWhiteSpace(credentials.BaseUrl) ||
                string.IsNullOrWhiteSpace(credentials.ClientId))
            {
                return null;
            }

            return credentials with
            {
                BaseUrl = credentials.BaseUrl.Trim().TrimEnd('/'),
                ClientId = credentials.ClientId.Trim(),
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(NyxIdCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.AccessToken);

        var directory = Path.GetDirectoryName(FilePath)
                        ?? throw new InvalidOperationException("NyxID credential path has no parent directory.");
        Directory.CreateDirectory(directory);

        var normalized = credentials with
        {
            BaseUrl = credentials.BaseUrl.Trim().TrimEnd('/'),
            ClientId = credentials.ClientId.Trim(),
            AccessToken = credentials.AccessToken.Trim(),
            RefreshToken = credentials.RefreshToken?.Trim(),
            IdToken = credentials.IdToken?.Trim(),
        };

        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(normalized, SerializerOptions);
        File.WriteAllText(tempPath, json);
        ApplyUnixPermissions(tempPath);
        File.Move(tempPath, FilePath, overwrite: true);
        ApplyUnixPermissions(FilePath);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
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
            // Ignore when the runtime does not expose Unix mode support.
        }
    }
}
