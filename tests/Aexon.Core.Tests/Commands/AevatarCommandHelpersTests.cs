using Aexon.Commands;

namespace Aexon.Core.Tests.Commands;

/// <summary>
/// Pure-helper tests for <see cref="AevatarCommand"/>. The command itself is
/// full of console I/O and HTTP wiring that we exercise elsewhere; these tests
/// pin down the small formatting + parsing functions that drive the UX.
/// </summary>
public sealed class AevatarCommandHelpersTests
{
    // ── FormatConversationId ──

    [Fact]
    public void FormatConversationId_StripsNyxidPrefixAndCollapsesLongHex()
    {
        Assert.Equal("229b0df5…d49c", AevatarCommand.FormatConversationId("nyxid-chat-229b0df57d5c42a7b17f6759b50fd49c"));
    }

    [Fact]
    public void FormatConversationId_LeavesShortIdsAlone()
    {
        Assert.Equal("short", AevatarCommand.FormatConversationId("short"));
        Assert.Equal("1234567890abcdef", AevatarCommand.FormatConversationId("1234567890abcdef"));
    }

    [Fact]
    public void FormatConversationId_HandlesMissingPrefix()
    {
        Assert.Equal("229b0df5…0000", AevatarCommand.FormatConversationId("229b0df57d5c42a7b17f6759b50f0000"));
    }

    // ── SplitHead ──

    [Theory]
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    [InlineData("solo", "solo", "")]
    [InlineData("head rest of the line", "head", "rest of the line")]
    [InlineData("   head rest  ", "head", "rest")]
    public void SplitHead_SplitsAtFirstSpace(string input, string expectedHead, string expectedRest)
    {
        var (head, rest) = AevatarCommand.SplitHead(input);
        Assert.Equal(expectedHead, head);
        Assert.Equal(expectedRest, rest);
    }

    // ── Truncate ──

    [Fact]
    public void Truncate_ReturnsEmptyForNullOrEmpty()
    {
        Assert.Equal(string.Empty, AevatarCommand.Truncate(null, 10));
        Assert.Equal(string.Empty, AevatarCommand.Truncate(string.Empty, 10));
    }

    [Fact]
    public void Truncate_KeepsShortStrings()
    {
        Assert.Equal("hi", AevatarCommand.Truncate("hi", 10));
        Assert.Equal("exactly-10", AevatarCommand.Truncate("exactly-10", 10));
    }

    [Fact]
    public void Truncate_AppendsEllipsisWhenTooLong()
    {
        Assert.Equal("abcde…", AevatarCommand.Truncate("abcdefghij", 5));
    }

    // ── FormatTimestamp ──

    [Fact]
    public void FormatTimestamp_ParsesIsoAndRendersLocalDate()
    {
        var formatted = AevatarCommand.FormatTimestamp("2026-04-18T00:00:00Z");
        // Local-time formatting varies by machine, so just sanity-check shape.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", formatted);
    }

    [Fact]
    public void FormatTimestamp_PassesThroughUnparseableInput()
    {
        Assert.Equal("not-a-date", AevatarCommand.FormatTimestamp("not-a-date"));
    }

    // ── ExtractTitleFlag ──

    [Fact]
    public void ExtractTitleFlag_ReturnsNullForEmpty()
    {
        Assert.Null(AevatarCommand.ExtractTitleFlag(string.Empty));
        Assert.Null(AevatarCommand.ExtractTitleFlag("   "));
    }

    [Fact]
    public void ExtractTitleFlag_UsesBareTitleWhenNoFlag()
    {
        Assert.Equal("my chat", AevatarCommand.ExtractTitleFlag("my chat"));
    }

    [Fact]
    public void ExtractTitleFlag_HandlesTitleFlagWithValue()
    {
        Assert.Equal("my title", AevatarCommand.ExtractTitleFlag("--title my title"));
        Assert.Equal("my title", AevatarCommand.ExtractTitleFlag("--title=my title"));
    }

    [Fact]
    public void ExtractTitleFlag_StripsSurroundingQuotes()
    {
        Assert.Equal("my title", AevatarCommand.ExtractTitleFlag("--title \"my title\""));
        Assert.Equal("my title", AevatarCommand.ExtractTitleFlag("\"my title\""));
    }

    // ── ParseInvocationOptions ──

    [Fact]
    public void ParseInvocationOptions_ReturnsEmptyWhenArgsMissing()
    {
        var (endpoint, remaining, error) = AevatarCommand.ParseInvocationOptions(null);

        Assert.Null(endpoint);
        Assert.Equal(string.Empty, remaining);
        Assert.Null(error);
    }

    [Fact]
    public void ParseInvocationOptions_ParsesEndpointWithSeparateValue()
    {
        var (endpoint, remaining, error) =
            AevatarCommand.ParseInvocationOptions("--endpoint https://api.aevatar.local/v1 hello world");

        Assert.Equal("https://api.aevatar.local/v1", endpoint);
        Assert.Equal("hello world", remaining);
        Assert.Null(error);
    }

    [Fact]
    public void ParseInvocationOptions_ParsesEndpointWithEqualsSyntax()
    {
        var (endpoint, remaining, error) =
            AevatarCommand.ParseInvocationOptions("--endpoint=https://api.aevatar.local/ list");

        Assert.Equal("https://api.aevatar.local", endpoint);
        Assert.Equal("list", remaining);
        Assert.Null(error);
    }

    [Fact]
    public void ParseInvocationOptions_StopsConsumingAfterFirstPositionalToken()
    {
        var (endpoint, remaining, error) =
            AevatarCommand.ParseInvocationOptions("--endpoint https://api.aevatar.local web --port 7000");

        Assert.Equal("https://api.aevatar.local", endpoint);
        Assert.Equal("web --port 7000", remaining);
        Assert.Null(error);
    }

    [Fact]
    public void ParseInvocationOptions_RejectsMissingEndpointValue()
    {
        var (_, _, error) = AevatarCommand.ParseInvocationOptions("--endpoint");

        Assert.NotNull(error);
        Assert.Contains("Missing --endpoint value", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseInvocationOptions_RejectsInvalidEndpointValue()
    {
        var (_, _, error) = AevatarCommand.ParseInvocationOptions("--endpoint not-a-url hi");

        Assert.NotNull(error);
        Assert.Contains("Invalid --endpoint value", error, StringComparison.Ordinal);
    }
}
