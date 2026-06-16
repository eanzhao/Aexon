using Aexon.Commands;

namespace Aexon.Core.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="AevatarCommand.ParseWebFlags"/>.
/// Covers default-port behavior, flag parsing, and error paths.
/// </summary>
public sealed class AevatarWebFlagsTests
{
    [Fact]
    public void ParseWebFlags_DefaultPort_UsedWhenNoArgs()
    {
        var (port, noBrowser, endpoint, error) = AevatarCommand.ParseWebFlags("", defaultPort: 6688);

        Assert.Equal(6688, port);
        Assert.False(noBrowser);
        Assert.Null(endpoint);
        Assert.Null(error);
    }

    [Fact]
    public void ParseWebFlags_DifferentDefault_HonorsCallerDefault()
    {
        var (port, _, _, _) = AevatarCommand.ParseWebFlags("", defaultPort: 6689);

        Assert.Equal(6689, port);
    }

    [Fact]
    public void ParseWebFlags_PortFlag_SeparateValue_OverridesDefault()
    {
        var (port, _, _, error) = AevatarCommand.ParseWebFlags("--port 7000", defaultPort: 6688);

        Assert.Equal(7000, port);
        Assert.Null(error);
    }

    [Fact]
    public void ParseWebFlags_PortFlag_EqualsValue_OverridesDefault()
    {
        var (port, _, _, error) = AevatarCommand.ParseWebFlags("--port=7100", defaultPort: 6688);

        Assert.Equal(7100, port);
        Assert.Null(error);
    }

    [Fact]
    public void ParseWebFlags_NoBrowserFlag_FlipsBoolean()
    {
        var (_, noBrowser, _, error) = AevatarCommand.ParseWebFlags("--no-browser", defaultPort: 6688);

        Assert.True(noBrowser);
        Assert.Null(error);
    }

    [Fact]
    public void ParseWebFlags_BothFlags_BothApplied()
    {
        var (port, noBrowser, _, error) = AevatarCommand.ParseWebFlags("--no-browser --port 7200", defaultPort: 6688);

        Assert.Equal(7200, port);
        Assert.True(noBrowser);
        Assert.Null(error);
    }

    [Fact]
    public void ParseWebFlags_EndpointFlag_IsParsed()
    {
        var (port, noBrowser, endpoint, error) =
            AevatarCommand.ParseWebFlags("--endpoint https://api.aevatar.local --no-browser", defaultPort: 6688);

        Assert.Equal(6688, port);
        Assert.True(noBrowser);
        Assert.Equal("https://api.aevatar.local", endpoint);
        Assert.Null(error);
    }

    [Fact]
    public void ParseWebFlags_InvalidPort_ReturnsError()
    {
        var (_, _, _, error) = AevatarCommand.ParseWebFlags("--port abc", defaultPort: 6688);

        Assert.NotNull(error);
        Assert.Contains("Invalid --port value", error);
    }

    [Fact]
    public void ParseWebFlags_PortOutOfRange_ReturnsError()
    {
        var (_, _, _, error) = AevatarCommand.ParseWebFlags("--port 70000", defaultPort: 6688);

        Assert.NotNull(error);
        Assert.Contains("Invalid --port value", error);
    }

    [Fact]
    public void ParseWebFlags_UnknownFlag_ReturnsError()
    {
        var (_, _, _, error) = AevatarCommand.ParseWebFlags("--purple", defaultPort: 6688);

        Assert.NotNull(error);
        Assert.Contains("Unknown flag", error);
    }
}
