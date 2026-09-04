using Aexon.Cli;

namespace Aexon.Core.Tests.Cli;

/// <summary>
/// Covers CLI option parsing and non-interactive prompt composition.
/// </summary>
public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_RecognizesPrintModeOptions()
    {
        var options = CliOptions.Parse(
        [
            "--print",
            "--output-format", "json",
            "--approval-mode", "allow",
            "--max-turns", "7",
            "--provider", "openai",
            "review", "this", "repo"
        ]);

        Assert.True(options.PrintMode);
        Assert.Equal(NonInteractiveOutputFormat.Json, options.OutputFormat);
        Assert.Equal(NonInteractiveApprovalMode.Allow, options.ApprovalMode);
        Assert.Equal(7, options.MaxTurns);
        Assert.False(options.UseNyxId);
        Assert.Equal("openai", options.Provider);
        Assert.Equal("review this repo", options.InitialPrompt);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void Parse_RecognizesNyxIdFlag()
    {
        var options = CliOptions.Parse(["--provider", "anthropic", "--nyxid", "explain", "this", "repo"]);

        Assert.True(options.UseNyxId);
        Assert.Equal("anthropic", options.Provider);
        Assert.Equal("explain this repo", options.InitialPrompt);
    }

    [Fact]
    public void Parse_ReportsInvalidMaxTurns()
    {
        var options = CliOptions.Parse(["--print", "--max-turns", "zero"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--max-turns", options.ParseError, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsInvalidApprovalMode()
    {
        var options = CliOptions.Parse(["--print", "--approval-mode", "maybe"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--approval-mode", options.ParseError, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_UsesExplicitPromptWhenNoStdin()
    {
        var prompt = NonInteractivePromptBuilder.Compose("summarize this repo", null);

        Assert.Equal("summarize this repo", prompt);
    }

    [Fact]
    public void Compose_UsesStdinWhenPromptMissing()
    {
        var prompt = NonInteractivePromptBuilder.Compose(null, "from stdin");

        Assert.Equal("from stdin", prompt);
    }

    [Fact]
    public void Compose_WrapsStdinWhenPromptAndStdinAreBothPresent()
    {
        var prompt = NonInteractivePromptBuilder.Compose("review this code", "line 1\nline 2");

        Assert.Equal(
            "review this code\n\n<stdin>\nline 1\nline 2\n</stdin>",
            prompt);
    }
}
