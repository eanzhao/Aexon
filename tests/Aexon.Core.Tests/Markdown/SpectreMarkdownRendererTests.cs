using Aexon.Core.Markdown;

namespace Aexon.Core.Tests.Markdown;

public sealed class SpectreMarkdownRendererTests
{
    private readonly SpectreMarkdownRenderer _renderer = new();

    [Fact]
    public void Render_FormatsHeadingsInlineContentLinksAndEscapes()
    {
        const string markdown = """
            # Heading One

            This has **bold**, *italic*, `code`, [docs](https://example.com), and [square] brackets.
            """;

        var document = _renderer.Render(markdown);
        var preview = document.ToMarkupPreview();

        Assert.Contains("[bold yellow]Heading One[/]", preview);
        Assert.Contains("[bold]bold[/]", preview);
        Assert.Contains("[italic]italic[/]", preview);
        Assert.Contains("[black on silver]code[/]", preview);
        Assert.Contains("docs ([underline blue]https://example.com[/])", preview);
        Assert.Contains("[[square]]", preview);
    }

    [Fact]
    public void Render_FormatsListsAndBlockquotes()
    {
        const string markdown = """
            - first item
            - second item

            1. one
            2. two

            > quoted line
            > second line
            """;

        var preview = _renderer.Render(markdown).ToMarkupPreview();

        Assert.Contains("• first item", preview);
        Assert.Contains("• second item", preview);
        Assert.Contains("1. one", preview);
        Assert.Contains("2. two", preview);
        Assert.Contains("[grey]│[/] quoted line", preview);
        Assert.Contains("[grey]│[/] second line", preview);
    }

    [Theory]
    [InlineData("csharp", "public class Demo { }\n// comment", "[deepskyblue1]public[/]", "[grey]// comment[/]")]
    [InlineData("typescript", "const value: string = \"x\";\n// comment", "[deepskyblue1]const[/]", "[grey]// comment[/]")]
    [InlineData("python", "def run():\n    return True\n# comment", "[deepskyblue1]def[/]", "[grey]# comment[/]")]
    [InlineData("json", "{ \"enabled\": true, \"count\": 2 }", "[deepskyblue1]true[/]", "[yellow]2[/]")]
    [InlineData("yaml", "enabled: true\n# comment", "[deepskyblue1]true[/]", "[grey]# comment[/]")]
    [InlineData("bash", "if [ -f test.txt ]; then\n  echo \"ok\"\nfi\n# comment", "[deepskyblue1]if[/]", "[grey]# comment[/]")]
    public void Render_HighlightsSupportedCodeBlockLanguages(
        string language,
        string code,
        string expectedPrimaryToken,
        string expectedSecondaryToken)
    {
        var markdown = $"""
            ```{language}
            {code}
            ```
            """;

        var document = _renderer.Render(markdown);
        var codeBlock = Assert.IsType<SpectreCodeBlock>(Assert.Single(document.Blocks));

        Assert.Equal(language, codeBlock.Language);
        Assert.Contains(expectedPrimaryToken, codeBlock.Markup);
        Assert.Contains(expectedSecondaryToken, codeBlock.Markup);
    }

    [Fact]
    public void Render_ConvertsPipeTablesIntoTableBlocks()
    {
        const string markdown = """
            | Name | Value |
            | --- | --- |
            | Alpha | **1** |
            | Beta | `2` |
            """;

        var document = _renderer.Render(markdown);
        var table = Assert.IsType<SpectreTableBlock>(Assert.Single(document.Blocks));

        Assert.Equal(["Name", "Value"], table.Headers);
        Assert.Equal("Alpha", table.Rows[0][0]);
        Assert.Equal("[bold]1[/]", table.Rows[0][1]);
        Assert.Equal("[black on silver]2[/]", table.Rows[1][1]);
        Assert.Contains("Name | Value", document.ToMarkupPreview());
    }

    [Fact]
    public void Render_WhenDisabled_FallsBackToEscapedPlainText()
    {
        var renderer = new SpectreMarkdownRenderer(enabled: false);

        var document = renderer.Render("**bold** [tag]");
        var markup = Assert.IsType<SpectreMarkupBlock>(Assert.Single(document.Blocks));

        Assert.Equal("**bold** [[tag]]", markup.Markup);
    }
}
