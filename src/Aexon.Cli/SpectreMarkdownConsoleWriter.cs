using System.Text;
using Aexon.Core.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aexon.Cli;

/// <summary>
/// Buffers streaming markdown and renders complete blocks with Spectre.Console.
/// </summary>
internal sealed class SpectreMarkdownConsoleWriter
{
    private readonly IAnsiConsole _console;
    private readonly Action<string> _plainTextWriter;
    private readonly SpectreMarkdownRenderer _renderer;
    private readonly StringBuilder _pendingMarkdown = new();
    private readonly StringBuilder _pendingLine = new();
    private bool _insideCodeFence;

    public SpectreMarkdownConsoleWriter(
        IAnsiConsole console,
        Action<string> plainTextWriter,
        bool enabled = true)
    {
        _console = console;
        _plainTextWriter = plainTextWriter;
        _renderer = new SpectreMarkdownRenderer(enabled);
    }

    public bool Enabled => _renderer.Enabled;

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (!Enabled)
        {
            _plainTextWriter(text);
            return;
        }

        var normalized = text.ReplaceLineEndings("\n");
        foreach (var ch in normalized)
        {
            if (ch == '\n')
            {
                HandleCompletedLine(_pendingLine.ToString());
                _pendingLine.Clear();
                continue;
            }

            _pendingLine.Append(ch);
        }
    }

    public void WriteComplete(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return;

        if (!Enabled)
        {
            _plainTextWriter(markdown);
            return;
        }

        TryRenderChunk(markdown.ReplaceLineEndings("\n"));
    }

    public void Flush()
    {
        if (!Enabled)
            return;

        if (_pendingLine.Length > 0)
        {
            _pendingMarkdown.Append(_pendingLine);
            _pendingLine.Clear();
        }

        FlushPendingMarkdown();
    }

    private void HandleCompletedLine(string line)
    {
        _pendingMarkdown.AppendLine(line);
        if (IsFenceLine(line))
        {
            _insideCodeFence = !_insideCodeFence;
            if (!_insideCodeFence)
                FlushPendingMarkdown();

            return;
        }

        if (!_insideCodeFence && string.IsNullOrWhiteSpace(line))
            FlushPendingMarkdown();
    }

    private void FlushPendingMarkdown()
    {
        if (_pendingMarkdown.Length == 0)
            return;

        var chunk = _pendingMarkdown.ToString();
        _pendingMarkdown.Clear();
        TryRenderChunk(chunk);
    }

    private void TryRenderChunk(string markdown)
    {
        try
        {
            RenderDocument(_renderer.Render(markdown));
        }
        catch
        {
            _plainTextWriter(markdown);
        }
    }

    private void RenderDocument(SpectreMarkdownDocument document)
    {
        for (var index = 0; index < document.Blocks.Count; index++)
        {
            switch (document.Blocks[index])
            {
                case SpectreMarkupBlock markupBlock:
                    if (!string.IsNullOrWhiteSpace(markupBlock.Markup))
                        _console.Write(new Markup(markupBlock.Markup));
                    break;

                case SpectreCodeBlock codeBlock:
                    RenderCodeBlock(codeBlock);
                    break;

                case SpectreTableBlock tableBlock:
                    RenderTableBlock(tableBlock);
                    break;
            }

            _console.WriteLine();
        }
    }

    private void RenderCodeBlock(SpectreCodeBlock block)
    {
        var panel = new Panel(new Markup(block.Markup))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("grey"),
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
        };

        if (!string.IsNullOrWhiteSpace(block.Language))
            panel.Header = new PanelHeader(block.Language, Justify.Left);

        _console.Write(panel);
    }

    private void RenderTableBlock(SpectreTableBlock block)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Expand();

        foreach (var header in block.Headers)
            table.AddColumn(new TableColumn(new Markup(string.IsNullOrWhiteSpace(header) ? " " : header)));

        foreach (var row in block.Rows)
        {
            var renderables = row
                .Select(cell => (IRenderable)new Markup(string.IsNullOrWhiteSpace(cell) ? " " : cell))
                .ToArray();
            table.AddRow(renderables);
        }

        _console.Write(table);
    }

    private static bool IsFenceLine(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal);
}
