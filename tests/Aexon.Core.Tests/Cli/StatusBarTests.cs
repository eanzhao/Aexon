using Aexon.Cli;
using Aexon.Core.Messages;

namespace Aexon.Core.Tests.Cli;

/// <summary>
/// Covers status bar string formatting helpers.
/// </summary>
public sealed class StatusBarTests
{
    [Fact]
    public void StatusBar_Format_IncludesModelTokensAndDuration()
    {
        var line = StatusBar.Format(new StatusBarSnapshot(
            "claude-sonnet-4-6",
            new TokenUsage
            {
                InputTokens = 1_200,
                CacheReadInputTokens = 300,
                OutputTokens = 45,
            },
            TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(12))));

        Assert.Equal(
            "[status] model claude-sonnet-4-6 | tokens 1,500/45 in/out | session 03:12",
            line);
    }

    [Fact]
    public void StatusBar_FormatDuration_UsesHourClockWhenNeeded()
    {
        var formatted = StatusBar.FormatDuration(
            TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(2)).Add(TimeSpan.FromSeconds(3)));

        Assert.Equal("01:02:03", formatted);
    }
}
