using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Aexon.Core.Markdown;

/// <summary>
/// Converts Markdown into Spectre-friendly render blocks.
/// </summary>
public sealed class SpectreMarkdownRenderer
{
    private const string InlineCodeStyle = "black on silver";
    private const string HeadingLevelOneStyle = "bold yellow";
    private const string HeadingLevelTwoStyle = "bold deepskyblue1";
    private const string HeadingLevelThreeStyle = "bold green";
    private const string HeadingLevelFourStyle = "bold aqua";
    private const string HeadingLevelFiveStyle = "bold fuchsia";
    private const string HeadingLevelSixStyle = "bold silver";
    private const string KeywordStyle = "deepskyblue1";
    private const string StringStyle = "green";
    private const string NumberStyle = "yellow";
    private const string CommentStyle = "grey";
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();
    private static readonly IReadOnlyDictionary<string, SyntaxDefinition> SyntaxDefinitions =
        new Dictionary<string, SyntaxDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new(
                "//",
                [
                    "abstract", "as", "async", "await", "base", "bool", "break", "byte",
                    "case", "catch", "char", "checked", "class", "const", "continue",
                    "decimal", "default", "delegate", "do", "double", "else", "enum",
                    "event", "explicit", "extern", "false", "finally", "fixed", "float",
                    "for", "foreach", "if", "implicit", "in", "int", "interface", "internal",
                    "is", "lock", "long", "namespace", "new", "null", "object", "operator",
                    "out", "override", "params", "private", "protected", "public", "readonly",
                    "record", "ref", "return", "sealed", "short", "static", "string", "struct",
                    "switch", "this", "throw", "true", "try", "typeof", "using", "var", "virtual",
                    "void", "while"
                ]),
            ["typescript"] = new(
                "//",
                [
                    "any", "as", "async", "await", "boolean", "break", "case", "catch",
                    "class", "const", "continue", "declare", "default", "do", "else",
                    "enum", "export", "extends", "false", "finally", "for", "from",
                    "function", "if", "implements", "import", "in", "interface", "let",
                    "module", "new", "null", "number", "of", "private", "protected",
                    "public", "readonly", "return", "static", "string", "switch", "this",
                    "throw", "true", "try", "type", "typeof", "undefined", "var", "void",
                    "while"
                ]),
            ["python"] = new(
                "#",
                [
                    "and", "as", "assert", "async", "await", "break", "class", "continue",
                    "def", "del", "elif", "else", "except", "False", "finally", "for",
                    "from", "if", "import", "in", "is", "lambda", "None", "nonlocal",
                    "not", "or", "pass", "raise", "return", "True", "try", "while", "with",
                    "yield"
                ]),
            ["json"] = new(
                null,
                [
                    "false", "null", "true"
                ]),
            ["yaml"] = new(
                "#",
                [
                    "false", "no", "null", "off", "on", "true", "yes"
                ]),
            ["bash"] = new(
                "#",
                [
                    "case", "do", "done", "elif", "else", "esac", "fi", "for", "function",
                    "if", "in", "select", "then", "until", "while"
                ]),
        };

    public SpectreMarkdownRenderer(bool enabled = true)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }

    public SpectreMarkdownDocument Render(string markdown)
    {
        var normalized = (markdown ?? string.Empty).ReplaceLineEndings("\n");
        if (string.IsNullOrEmpty(normalized))
            return SpectreMarkdownDocument.Empty;

        if (!Enabled)
            return SpectreMarkdownDocument.FromPlainText(normalized);

        try
        {
            var parsed = FrontmatterParser.Parse(normalized);
            var document = Markdig.Markdown.Parse(parsed.Content, Pipeline);
            var blocks = new List<SpectreMarkdownBlock>();

            foreach (var block in document)
                AppendBlock(block, blocks);

            return blocks.Count == 0
                ? SpectreMarkdownDocument.FromPlainText(parsed.Content)
                : new SpectreMarkdownDocument(blocks);
        }
        catch
        {
            return SpectreMarkdownDocument.FromPlainText(normalized);
        }
    }

    public static string EscapeMarkup(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("[", "[[", StringComparison.Ordinal)
                .Replace("]", "]]", StringComparison.Ordinal);

    private static void AppendBlock(Block block, List<SpectreMarkdownBlock> blocks)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AppendMarkupBlock(
                    blocks,
                    WrapStyle(GetHeadingStyle(heading.Level), RenderInlineContainer(heading.Inline)));
                break;

            case ParagraphBlock paragraph:
                AppendMarkupBlock(blocks, RenderInlineContainer(paragraph.Inline));
                break;

            case QuoteBlock quote:
                AppendMarkupBlock(blocks, string.Join('\n', PrefixQuotedLines(RenderContainerLines(quote))));
                break;

            case ListBlock list:
                AppendMarkupBlock(blocks, string.Join('\n', RenderListLines(list, 0)));
                break;

            case Table table:
                var tableBlock = RenderTable(table);
                if (tableBlock != null)
                    blocks.Add(tableBlock);
                break;

            case FencedCodeBlock fencedCode:
                blocks.Add(RenderCodeBlock(fencedCode, ExtractLanguage(fencedCode.Info)));
                break;

            case CodeBlock codeBlock:
                blocks.Add(RenderCodeBlock(codeBlock, null));
                break;

            case ThematicBreakBlock:
                AppendMarkupBlock(blocks, "[grey]────────────────[/]");
                break;

            default:
                var fallbackLines = RenderBlockLines(block, 0);
                if (fallbackLines.Count > 0)
                    AppendMarkupBlock(blocks, string.Join('\n', fallbackLines));
                break;
        }
    }

    private static void AppendMarkupBlock(List<SpectreMarkdownBlock> blocks, string markup)
    {
        if (!string.IsNullOrWhiteSpace(markup))
            blocks.Add(new SpectreMarkupBlock(markup));
    }

    private static SpectreCodeBlock RenderCodeBlock(CodeBlock block, string? language)
    {
        var code = block.Lines.ToString() ?? string.Empty;
        var normalizedLanguage = NormalizeLanguage(language);
        return new SpectreCodeBlock(
            normalizedLanguage,
            code,
            HighlightCode(code, normalizedLanguage));
    }

    private static SpectreTableBlock? RenderTable(Table table)
    {
        var headers = new List<string>();
        var rows = new List<IReadOnlyList<string>>();

        foreach (var rowObject in table)
        {
            if (rowObject is not TableRow row)
                continue;

            var renderedCells = new List<string>();
            foreach (var cellObject in row)
            {
                if (cellObject is not TableCell cell)
                    continue;

                renderedCells.Add(string.Join('\n', RenderContainerLines(cell)));
            }

            if (row.IsHeader)
            {
                headers.Clear();
                headers.AddRange(renderedCells);
                continue;
            }

            rows.Add(renderedCells);
        }

        if (headers.Count == 0)
        {
            var columnCount = rows.Count == 0 ? 0 : rows.Max(static row => row.Count);
            for (var index = 0; index < columnCount; index++)
                headers.Add($"Column {index + 1}");
        }

        if (headers.Count == 0)
            return null;

        var normalizedRows = rows
            .Select(row => NormalizeTableRow(row, headers.Count))
            .ToList();

        return new SpectreTableBlock(headers, normalizedRows);
    }

    private static IReadOnlyList<string> NormalizeTableRow(
        IReadOnlyList<string> row,
        int expectedColumns)
    {
        if (row.Count == expectedColumns)
            return row;

        var cells = row.ToList();
        while (cells.Count < expectedColumns)
            cells.Add(string.Empty);

        if (cells.Count > expectedColumns)
            cells.RemoveRange(expectedColumns, cells.Count - expectedColumns);

        return cells;
    }

    private static IReadOnlyList<string> RenderContainerLines(ContainerBlock container)
    {
        var lines = new List<string>();
        foreach (var child in container)
        {
            if (child is Block block)
                lines.AddRange(RenderBlockLines(block, 0));
        }

        return lines;
    }

    private static IReadOnlyList<string> RenderBlockLines(Block block, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return SplitLines(
                    ApplyIndent(
                        WrapStyle(GetHeadingStyle(heading.Level), RenderInlineContainer(heading.Inline)),
                        depth));

            case ParagraphBlock paragraph:
                return SplitLines(ApplyIndent(RenderInlineContainer(paragraph.Inline), depth));

            case QuoteBlock quote:
                return PrefixQuotedLines(RenderContainerLines(quote), depth);

            case ListBlock list:
                return RenderListLines(list, depth);

            case FencedCodeBlock fencedCode:
                return SplitLines(ApplyIndent(HighlightCode(fencedCode.Lines.ToString() ?? string.Empty, ExtractLanguage(fencedCode.Info)), depth));

            case CodeBlock codeBlock:
                return SplitLines(ApplyIndent(HighlightCode(codeBlock.Lines.ToString() ?? string.Empty, null), depth));

            case Table table:
                var tableBlock = RenderTable(table);
                return tableBlock == null
                    ? []
                    : tableBlock.Rows.Count == 0
                        ? [ApplyIndent(string.Join(" | ", tableBlock.Headers), depth)]
                        : new[] { ApplyIndent(string.Join(" | ", tableBlock.Headers), depth) }
                            .Concat(tableBlock.Rows.Select(row => ApplyIndent(string.Join(" | ", row), depth)))
                            .ToArray();

            case ThematicBreakBlock:
                return [ApplyIndent("[grey]────────────────[/]", depth)];

            default:
                var text = EscapeMarkup(block.ToString() ?? string.Empty);
                return string.IsNullOrWhiteSpace(text)
                    ? []
                    : [ApplyIndent(text, depth)];
        }
    }

    private static IReadOnlyList<string> RenderListLines(ListBlock list, int depth)
    {
        var lines = new List<string>();
        var itemNumber = list.IsOrdered && int.TryParse(list.OrderedStart, out var parsedItemNumber)
            ? parsedItemNumber
            : 1;

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
                continue;

            var prefix = list.IsOrdered ? $"{itemNumber}. " : "• ";
            lines.AddRange(RenderListItemLines(item, prefix, depth));
            itemNumber++;
        }

        return lines;
    }

    private static IReadOnlyList<string> RenderListItemLines(
        ListItemBlock item,
        string prefix,
        int depth)
    {
        var itemLines = new List<string>();
        foreach (var child in item)
        {
            if (child is Block block)
                itemLines.AddRange(RenderBlockLines(block, 0));
        }

        if (itemLines.Count == 0)
            itemLines.Add(string.Empty);

        var indent = new string(' ', depth * 2);
        var continuation = new string(' ', prefix.Length);
        var lines = new List<string>
        {
            $"{indent}{prefix}{itemLines[0]}"
        };

        for (var index = 1; index < itemLines.Count; index++)
            lines.Add($"{indent}{continuation}{itemLines[index]}");

        return lines;
    }

    private static IReadOnlyList<string> PrefixQuotedLines(
        IReadOnlyList<string> lines,
        int depth = 0)
    {
        var indent = new string(' ', depth * 2);
        return lines.Count == 0
            ? [$"{indent}[grey]│[/]"]
            : lines.Select(line => $"{indent}[grey]│[/] {line}").ToArray();
    }

    private static string ApplyIndent(string markup, int depth)
    {
        if (depth <= 0 || string.IsNullOrEmpty(markup))
            return markup;

        var indent = new string(' ', depth * 2);
        return string.Join('\n', SplitLines(markup).Select(line => indent + line));
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        (text ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n')
            .ToArray();

    private static string RenderInlineContainer(ContainerInline? container)
    {
        if (container == null)
            return string.Empty;

        var builder = new StringBuilder();
        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
            builder.Append(RenderInline(inline));

        return builder.ToString();
    }

    private static string RenderInline(Inline inline) =>
        inline switch
        {
            LiteralInline literal => EscapeMarkup(literal.Content.ToString()),
            CodeInline code => WrapStyle(InlineCodeStyle, EscapeMarkup(code.Content)),
            LineBreakInline => "\n",
            LinkInline link when link.IsImage => RenderInlineContainer(link),
            LinkInline link => RenderLink(link),
            EmphasisInline emphasis => RenderEmphasis(emphasis),
            ContainerInline container => RenderInlineContainer(container),
            _ => EscapeMarkup(inline.ToString() ?? string.Empty),
        };

    private static string RenderLink(LinkInline link)
    {
        var label = RenderInlineContainer(link);
        var url = EscapeMarkup(link.Url ?? string.Empty);
        if (string.IsNullOrWhiteSpace(label))
            return string.IsNullOrWhiteSpace(url) ? string.Empty : $"[underline blue]{url}[/]";

        return string.IsNullOrWhiteSpace(url)
            ? label
            : $"{label} ([underline blue]{url}[/])";
    }

    private static string RenderEmphasis(EmphasisInline emphasis)
    {
        var content = RenderInlineContainer(emphasis);
        return emphasis.DelimiterCount switch
        {
            >= 3 => WrapStyle("bold italic", content),
            2 => WrapStyle("bold", content),
            _ => WrapStyle("italic", content),
        };
    }

    private static string HighlightCode(string code, string? language)
    {
        var normalized = (code ?? string.Empty).ReplaceLineEndings("\n");
        if (normalized.Length == 0)
            return string.Empty;

        if (!SyntaxDefinitions.TryGetValue(NormalizeLanguage(language), out var definition))
            return EscapeMarkup(normalized);

        var lines = normalized.Split('\n');
        var highlighted = lines.Select(line => HighlightLine(line, definition));
        return string.Join('\n', highlighted);
    }

    private static string HighlightLine(string line, SyntaxDefinition definition)
    {
        if (line.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        var index = 0;
        while (index < line.Length)
        {
            if (!string.IsNullOrEmpty(definition.CommentPrefix) &&
                line.AsSpan(index).StartsWith(definition.CommentPrefix, StringComparison.Ordinal))
            {
                builder.Append(WrapStyle(CommentStyle, EscapeMarkup(line[index..])));
                break;
            }

            var ch = line[index];
            if (ch is '"' or '\'')
            {
                var quoted = ReadQuotedToken(line, ref index);
                builder.Append(WrapStyle(StringStyle, EscapeMarkup(quoted)));
                continue;
            }

            if (char.IsDigit(ch))
            {
                var number = ReadWhile(line, ref index, static candidate => char.IsDigit(candidate) || candidate is '.' or '_');
                builder.Append(WrapStyle(NumberStyle, EscapeMarkup(number)));
                continue;
            }

            if (IsIdentifierStart(ch))
            {
                var identifier = ReadWhile(line, ref index, IsIdentifierPart);
                builder.Append(
                    definition.Keywords.Contains(identifier)
                        ? WrapStyle(KeywordStyle, EscapeMarkup(identifier))
                        : EscapeMarkup(identifier));
                continue;
            }

            builder.Append(EscapeMarkup(ch.ToString()));
            index++;
        }

        return builder.ToString();
    }

    private static string ReadQuotedToken(string line, ref int index)
    {
        var quote = line[index];
        var start = index++;
        while (index < line.Length)
        {
            if (line[index] == '\\' && index + 1 < line.Length)
            {
                index += 2;
                continue;
            }

            if (line[index] == quote)
            {
                index++;
                break;
            }

            index++;
        }

        return line[start..index];
    }

    private static string ReadWhile(string line, ref int index, Func<char, bool> predicate)
    {
        var start = index;
        while (index < line.Length && predicate(line[index]))
            index++;

        return line[start..index];
    }

    private static bool IsIdentifierStart(char ch) =>
        char.IsLetter(ch) || ch is '_' or '$';

    private static bool IsIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '$';

    private static string WrapStyle(string style, string content) =>
        string.IsNullOrEmpty(content) ? string.Empty : $"[{style}]{content}[/]";

    private static string GetHeadingStyle(int level) =>
        level switch
        {
            1 => HeadingLevelOneStyle,
            2 => HeadingLevelTwoStyle,
            3 => HeadingLevelThreeStyle,
            4 => HeadingLevelFourStyle,
            5 => HeadingLevelFiveStyle,
            _ => HeadingLevelSixStyle,
        };

    private static string NormalizeLanguage(string? language)
    {
        var value = language?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.ToLowerInvariant() switch
        {
            "c#" or "cs" or "csharp" => "csharp",
            "ts" or "tsx" or "typescript" => "typescript",
            "py" or "python" => "python",
            "json" => "json",
            "yaml" or "yml" => "yaml",
            "bash" or "shell" or "sh" or "zsh" => "bash",
            _ => value.ToLowerInvariant(),
        };
    }

    private static string? ExtractLanguage(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
            return null;

        var parts = info.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts[0];
    }

    private sealed record SyntaxDefinition(
        string? CommentPrefix,
        HashSet<string> Keywords);
}

public sealed record SpectreMarkdownDocument(IReadOnlyList<SpectreMarkdownBlock> Blocks)
{
    public static SpectreMarkdownDocument Empty { get; } = new([]);

    public static SpectreMarkdownDocument FromPlainText(string markdown) =>
        new([new SpectreMarkupBlock(SpectreMarkdownRenderer.EscapeMarkup(markdown))]);

    public string ToMarkupPreview()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < Blocks.Count; index++)
        {
            switch (Blocks[index])
            {
                case SpectreMarkupBlock markup:
                    builder.Append(markup.Markup);
                    break;

                case SpectreCodeBlock code:
                    builder.Append(code.Markup);
                    break;

                case SpectreTableBlock table:
                    builder.Append(string.Join(" | ", table.Headers));
                    if (table.Rows.Count > 0)
                        builder.Append('\n');

                    for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                    {
                        builder.Append(string.Join(" | ", table.Rows[rowIndex]));
                        if (rowIndex < table.Rows.Count - 1)
                            builder.Append('\n');
                    }

                    break;
            }

            if (index < Blocks.Count - 1)
                builder.Append("\n\n");
        }

        return builder.ToString();
    }
}

public abstract record SpectreMarkdownBlock;

public sealed record SpectreMarkupBlock(string Markup) : SpectreMarkdownBlock;

public sealed record SpectreCodeBlock(string Language, string Code, string Markup) : SpectreMarkdownBlock;

public sealed record SpectreTableBlock(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows) : SpectreMarkdownBlock;
