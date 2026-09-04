using Aexon.Core.Auth;
using Aexon.Core.Tests.Runtime;

namespace Aexon.Core.Tests.Auth;

public sealed class NyxIdCredentialStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsCredentials()
    {
        using var temp = new TempDirectory();
        var store = new NyxIdCredentialStore(temp.FullPath("nyxid.json"));
        var expected = new NyxIdCredentials
        {
            BaseUrl = "https://nyx.example",
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            IdToken = "id-token",
            ExpiresAt = DateTimeOffset.Parse("2026-04-17T12:00:00Z"),
            ClientId = "client-123",
        };

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(expected, actual);
        Assert.True(File.Exists(store.FilePath));

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(store.FilePath);
            Assert.True(mode.HasFlag(UnixFileMode.UserRead));
            Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
            Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
            Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
            Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
            Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        }
    }

    [Fact]
    public void Load_ReturnsNullWhenFileIsMissing()
    {
        using var temp = new TempDirectory();
        var store = new NyxIdCredentialStore(temp.FullPath("missing.json"));

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ReturnsNullWhenFileIsCorrupt()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("nyxid.json", "{ this is not valid json");
        var store = new NyxIdCredentialStore(path);

        Assert.Null(store.Load());
    }
}
