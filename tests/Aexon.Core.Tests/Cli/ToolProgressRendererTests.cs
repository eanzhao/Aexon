using System.Text.Json;
using Aexon.Cli;

namespace Aexon.Core.Tests.Cli;

/// <summary>
/// Covers tool progress formatting helpers.
/// </summary>
public sealed class ToolProgressRendererTests
{
    [Fact]
    public void ProgressSummary_PrioritizesFilePathAndCompactsLargeContent()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            old_string = "before text",
            file_path = "/tmp/demo.txt",
            new_string = new string('x', 40),
        });

        var summary = ToolProgressRenderer.SummarizeParameters(input);

        Assert.StartsWith("file_path=/tmp/demo.txt", summary);
        Assert.Contains("new_string=<40 chars>", summary, StringComparison.Ordinal);
        Assert.Contains("old_string=<11 chars>", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressSummary_TruncatesLongPayloads()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            command = new string('x', 80),
            description = new string('y', 80),
        });

        var summary = ToolProgressRenderer.SummarizeParameters(input, maxLength: 40);

        Assert.Equal(40, summary.Length);
        Assert.EndsWith("...", summary, StringComparison.Ordinal);
    }
}
