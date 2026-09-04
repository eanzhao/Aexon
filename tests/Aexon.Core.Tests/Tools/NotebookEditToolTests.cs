using System.Text.Json;
using System.Text.Json.Nodes;
using Aexon.Core.Permissions;
using Aexon.Core.Tests.Runtime;
using Aexon.Core.Tools;
using Aexon.Tools;

namespace Aexon.Core.Tests.Tools;

public sealed class NotebookEditToolTests
{
    [Fact]
    public async Task NotebookEditTool_InsertAtEnd_AppendsCodeCellAndReportsPermissionSurface()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(
            MarkdownCell("first"),
            MarkdownCell("second")));
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 2,
                cell_type = "code",
                content = "print('done')",
            }),
            CreateContext(temp.Root));
        var permission = await tool.CheckPermissionsAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 2,
                cell_type = "code",
                content = "print('done')",
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);
        Assert.Equal(PermissionBehavior.Ask, permission.Behavior);
        Assert.Contains(notebookPath, permission.Message, StringComparison.Ordinal);
        Assert.Equal("Editing sample.ipynb", tool.GetActivityDescription(Json(new { file_path = notebookPath })));

        var notebook = ReadNotebook(notebookPath);
        var cells = notebook.GetProperty("cells");
        Assert.Equal(3, cells.GetArrayLength());
        Assert.Equal("code", cells[2].GetProperty("cell_type").GetString());
        Assert.Equal("print('done')", cells[2].GetProperty("source").GetString());
        Assert.Equal(0, cells[2].GetProperty("outputs").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, cells[2].GetProperty("execution_count").ValueKind);
    }

    [Fact]
    public async Task NotebookEditTool_InsertAtMiddle_InsertsCellWithoutTouchingNeighbors()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(
            MarkdownCell("first"),
            MarkdownCell("third")));
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 1,
                cell_type = "markdown",
                content = "second",
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);

        var cells = ReadNotebook(notebookPath).GetProperty("cells");
        Assert.Equal("first", cells[0].GetProperty("source").GetString());
        Assert.Equal("second", cells[1].GetProperty("source").GetString());
        Assert.Equal("third", cells[2].GetProperty("source").GetString());
    }

    [Fact]
    public async Task NotebookEditTool_InsertAtZero_PrependsCell()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(
            MarkdownCell("first"),
            MarkdownCell("second")));
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 0,
                cell_type = "markdown",
                content = "zeroth",
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);

        var cells = ReadNotebook(notebookPath).GetProperty("cells");
        Assert.Equal("zeroth", cells[0].GetProperty("source").GetString());
        Assert.Equal("first", cells[1].GetProperty("source").GetString());
        Assert.Equal("second", cells[2].GetProperty("source").GetString());
    }

    [Fact]
    public async Task NotebookEditTool_ReplaceCodeCell_ClearsOutputsAndExecutionCount()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(
            new JsonObject
            {
                ["cell_type"] = "code",
                ["metadata"] = new JsonObject
                {
                    ["custom"] = 42,
                },
                ["source"] = "print('before')",
                ["outputs"] = new JsonArray(
                    new JsonObject
                    {
                        ["output_type"] = "stream",
                        ["name"] = "stdout",
                        ["text"] = "before\n",
                    }),
                ["execution_count"] = 7,
            }));
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "replace",
                index = 0,
                cell_type = "code",
                content = "print('after')",
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);

        var cell = ReadNotebook(notebookPath).GetProperty("cells")[0];
        Assert.Equal("code", cell.GetProperty("cell_type").GetString());
        Assert.Equal("print('after')", cell.GetProperty("source").GetString());
        Assert.Equal(0, cell.GetProperty("outputs").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("execution_count").ValueKind);
        Assert.Equal(42, cell.GetProperty("metadata").GetProperty("custom").GetInt32());
    }

    [Fact]
    public async Task NotebookEditTool_ReplaceMarkdownCell_UpdatesContent()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(
            new JsonObject
            {
                ["cell_type"] = "markdown",
                ["metadata"] = new JsonObject
                {
                    ["collapsed"] = true,
                },
                ["source"] = new JsonArray("before", "\n"),
            }));
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "replace",
                index = 0,
                cell_type = "markdown",
                content = "after",
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);

        var cell = ReadNotebook(notebookPath).GetProperty("cells")[0];
        Assert.Equal("markdown", cell.GetProperty("cell_type").GetString());
        Assert.Equal("after", cell.GetProperty("source").GetString());
        Assert.True(cell.GetProperty("metadata").GetProperty("collapsed").GetBoolean());
        Assert.False(cell.TryGetProperty("outputs", out _));
        Assert.False(cell.TryGetProperty("execution_count", out _));
    }

    [Fact]
    public async Task NotebookEditTool_DeleteMiddleCell_RemovesTargetCellOnly()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(
            MarkdownCell("first"),
            MarkdownCell("second"),
            MarkdownCell("third")));
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "delete",
                index = 1,
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);

        var cells = ReadNotebook(notebookPath).GetProperty("cells");
        Assert.Equal(2, cells.GetArrayLength());
        Assert.Equal("first", cells[0].GetProperty("source").GetString());
        Assert.Equal("third", cells[1].GetProperty("source").GetString());
    }

    [Fact]
    public async Task NotebookEditTool_OutOfRangeIndices_ReturnErrors()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(MarkdownCell("only")));
        var tool = new NotebookEditTool();

        var insertResult = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 2,
                cell_type = "markdown",
                content = "x",
            }),
            CreateContext(temp.Root));
        var replaceResult = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "replace",
                index = 1,
                cell_type = "markdown",
                content = "x",
            }),
            CreateContext(temp.Root));
        var deleteResult = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "delete",
                index = -1,
            }),
            CreateContext(temp.Root));

        Assert.True(insertResult.IsError);
        Assert.Contains("out of range", insertResult.Data, StringComparison.Ordinal);
        Assert.True(replaceResult.IsError);
        Assert.Contains("out of range", replaceResult.Data, StringComparison.Ordinal);
        Assert.True(deleteResult.IsError);
        Assert.Contains("zero or greater", deleteResult.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotebookEditTool_InvalidJson_ReturnsCleanError()
    {
        using var temp = new TempDirectory();
        var notebookPath = temp.WriteFile("broken.ipynb", "{not json");
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 0,
                cell_type = "markdown",
                content = "x",
            }),
            CreateContext(temp.Root));

        Assert.True(result.IsError);
        Assert.Contains("Invalid notebook JSON", result.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotebookEditTool_PreservesUnknownCellMetadataThroughRoundTrip()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, CreateNotebook(
            new JsonObject
            {
                ["cell_type"] = "markdown",
                ["metadata"] = new JsonObject
                {
                    ["custom"] = new JsonObject
                    {
                        ["level"] = 3,
                    },
                },
                ["attachments"] = new JsonObject
                {
                    ["plot.png"] = new JsonObject
                    {
                        ["image/png"] = "ZmFrZQ==",
                    },
                },
                ["source"] = new JsonArray("first", "\n"),
            },
            MarkdownCell("second")));
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 2,
                cell_type = "markdown",
                content = "third",
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);

        var preservedCell = ReadNotebook(notebookPath).GetProperty("cells")[0];
        Assert.Equal(3, preservedCell.GetProperty("metadata").GetProperty("custom").GetProperty("level").GetInt32());
        Assert.Equal(
            "ZmFrZQ==",
            preservedCell.GetProperty("attachments").GetProperty("plot.png").GetProperty("image/png").GetString());
        Assert.Equal(JsonValueKind.Array, preservedCell.GetProperty("source").ValueKind);
    }

    [Fact]
    public async Task NotebookEditTool_PreservesNotebookLevelMetadataThroughRoundTrip()
    {
        using var temp = new TempDirectory();
        var notebookPath = WriteNotebook(temp, new JsonObject
        {
            ["cells"] = new JsonArray(MarkdownCell("first")),
            ["metadata"] = new JsonObject
            {
                ["kernelspec"] = new JsonObject
                {
                    ["display_name"] = "Python 3",
                    ["name"] = "python3",
                },
                ["language_info"] = new JsonObject
                {
                    ["name"] = "python",
                    ["version"] = "3.12",
                },
            },
            ["nbformat"] = 4,
            ["nbformat_minor"] = 5,
        });
        var tool = new NotebookEditTool();

        var result = await tool.ExecuteAsync(
            Json(new
            {
                file_path = notebookPath,
                action = "insert",
                index = 1,
                cell_type = "markdown",
                content = "second",
            }),
            CreateContext(temp.Root));

        Assert.False(result.IsError);

        var notebook = ReadNotebook(notebookPath);
        Assert.Equal(4, notebook.GetProperty("nbformat").GetInt32());
        Assert.Equal(5, notebook.GetProperty("nbformat_minor").GetInt32());
        Assert.Equal(
            "Python 3",
            notebook.GetProperty("metadata").GetProperty("kernelspec").GetProperty("display_name").GetString());
        Assert.Equal(
            "python",
            notebook.GetProperty("metadata").GetProperty("language_info").GetProperty("name").GetString());
    }

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value);

    private static ToolExecutionContext CreateContext(string workingDirectory) =>
        new()
        {
            WorkingDirectory = workingDirectory,
            PermissionContext = new PermissionContext
            {
                WorkingDirectory = workingDirectory,
            },
            Tools = [],
            Messages = [],
            CancellationToken = CancellationToken.None,
        };

    private static string WriteNotebook(TempDirectory temp, JsonObject notebook)
    {
        var path = temp.FullPath("sample.ipynb");
        File.WriteAllText(path, notebook.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static JsonElement ReadNotebook(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    private static JsonObject CreateNotebook(params JsonObject[] cells) =>
        new()
        {
            ["cells"] = new JsonArray(cells),
            ["metadata"] = new JsonObject
            {
                ["kernelspec"] = new JsonObject
                {
                    ["display_name"] = "Python 3",
                },
            },
            ["nbformat"] = 4,
            ["nbformat_minor"] = 5,
        };

    private static JsonObject MarkdownCell(string content) =>
        new()
        {
            ["cell_type"] = "markdown",
            ["metadata"] = new JsonObject(),
            ["source"] = content,
        };
}
