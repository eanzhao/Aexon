using System.Text.Json;
using System.Text.Json.Nodes;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Edits Jupyter notebooks by inserting, replacing, or deleting cells.
/// </summary>
public class NotebookEditTool : ITool
{
    public string Name => "NotebookEdit";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Edit a Jupyter notebook by inserting, replacing, or deleting cells.");

    public JsonElement GetInputSchema()
    {
        var schema = """
        {
            "type": "object",
            "properties": {
                "file_path": {
                    "type": "string",
                    "description": "Absolute path to the notebook file"
                },
                "action": {
                    "type": "string",
                    "enum": ["insert", "replace", "delete"],
                    "description": "Notebook edit operation to apply"
                },
                "index": {
                    "type": "integer",
                    "description": "Zero-based cell index to insert at, replace, or delete"
                },
                "cell_type": {
                    "type": "string",
                    "enum": ["code", "markdown"],
                    "description": "Cell type for insert or replace operations"
                },
                "content": {
                    "type": "string",
                    "description": "Cell source content for insert or replace operations"
                }
            },
            "required": ["file_path", "action", "index"],
            "additionalProperties": false
        }
        """;
        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            Edit a Jupyter notebook by operating on whole cells.

            Rules:
            - file_path should point to the notebook file to edit
            - insert adds a new cell at index and shifts later cells down
            - replace overwrites the target cell content and type
            - delete removes the target cell
            - New and replaced code cells start with empty outputs and null execution_count
            """);
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken ct = default)
    {
        var filePath = input.GetProperty("file_path").GetString();
        var action = input.GetProperty("action").GetString();
        var index = input.GetProperty("index").GetInt32();

        if (string.IsNullOrWhiteSpace(filePath))
            return ToolResult.Error("file_path is required.");

        if (string.IsNullOrWhiteSpace(action))
            return ToolResult.Error("action is required.");

        if (index < 0)
            return ToolResult.Error("index must be zero or greater.");

        filePath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(Path.Combine(context.WorkingDirectory, filePath));

        if (!File.Exists(filePath))
            return ToolResult.Error($"File not found: {filePath}");

        try
        {
            var notebookContent = await File.ReadAllTextAsync(filePath, ct);
            var root = JsonNode.Parse(notebookContent) as JsonObject;
            if (root is null)
                return ToolResult.Error("Notebook must be a JSON object.");

            if (root["cells"] is not JsonArray cells)
                return ToolResult.Error("Notebook must contain a top-level 'cells' array.");

            var normalizedAction = NormalizeAction(action);
            if (normalizedAction is null)
                return ToolResult.Error("action must be one of: insert, replace, delete.");

            switch (normalizedAction)
            {
                case "insert":
                {
                    if (!TryGetCellInput(input, out var cellType, out var content, out var error))
                        return ToolResult.Error(error!);

                    if (index > cells.Count)
                        return ToolResult.Error(
                            $"index {index} is out of range for insert. Valid range is 0-{cells.Count}.");

                    cells.Insert(index, CreateCell(cellType!, content!));
                    break;
                }
                case "replace":
                {
                    if (!TryGetCellInput(input, out var cellType, out var content, out var error))
                        return ToolResult.Error(error!);

                    if (index >= cells.Count)
                        return ToolResult.Error(
                            $"index {index} is out of range for replace. Valid range is 0-{cells.Count - 1}.");

                    if (cells[index] is not JsonObject existingCell)
                        return ToolResult.Error($"Cell at index {index} is not a JSON object.");

                    cells[index] = ReplaceCell(existingCell, cellType!, content!);
                    break;
                }
                case "delete":
                {
                    if (index >= cells.Count)
                        return ToolResult.Error(
                            $"index {index} is out of range for delete. Valid range is 0-{cells.Count - 1}.");

                    cells.RemoveAt(index);
                    break;
                }
            }

            var updatedContent = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            await File.WriteAllTextAsync(filePath, updatedContent, ct);

            return ToolResult.Success(
                $"{ToPastTense(normalizedAction)} notebook {filePath} at cell index {index}.");
        }
        catch (JsonException ex)
        {
            return ToolResult.Error($"Invalid notebook JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error editing notebook: {ex.Message}");
        }
    }

    public Task<PermissionResult> CheckPermissionsAsync(
        JsonElement input,
        ToolExecutionContext context)
    {
        var filePath = input.TryGetProperty("file_path", out var fp)
            ? fp.GetString() ?? string.Empty
            : string.Empty;
        return Task.FromResult(PermissionResult.Ask($"Allow editing: {filePath}"));
    }

    public string? GetActivityDescription(JsonElement? input)
    {
        if (input?.TryGetProperty("file_path", out var fp) == true)
            return $"Editing {Path.GetFileName(fp.GetString() ?? string.Empty)}";

        return "Editing notebook";
    }

    private static string? NormalizeAction(string action) =>
        action.ToLowerInvariant() switch
        {
            "insert" => "insert",
            "replace" => "replace",
            "delete" => "delete",
            _ => null,
        };

    private static bool TryGetCellInput(
        JsonElement input,
        out string? cellType,
        out string? content,
        out string? error)
    {
        cellType = input.TryGetProperty("cell_type", out var cellTypeElement)
            ? NormalizeCellType(cellTypeElement.GetString())
            : null;
        content = input.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString()
            : null;
        error = null;

        if (cellType is null)
        {
            error = "cell_type must be either 'code' or 'markdown' for insert and replace.";
            return false;
        }

        if (content is null)
        {
            error = "content is required for insert and replace.";
            return false;
        }

        return true;
    }

    private static string? NormalizeCellType(string? cellType) =>
        cellType?.ToLowerInvariant() switch
        {
            "code" => "code",
            "markdown" => "markdown",
            _ => null,
        };

    private static JsonObject CreateCell(string cellType, string content)
    {
        var cell = new JsonObject
        {
            ["cell_type"] = cellType,
            ["metadata"] = new JsonObject(),
            ["source"] = content,
        };

        if (string.Equals(cellType, "code", StringComparison.Ordinal))
        {
            cell["outputs"] = new JsonArray();
            cell["execution_count"] = null;
        }

        return cell;
    }

    private static JsonObject ReplaceCell(JsonObject existingCell, string cellType, string content)
    {
        var updatedCell = (JsonObject)existingCell.DeepClone();
        updatedCell["cell_type"] = cellType;
        updatedCell["source"] = content;

        if (updatedCell["metadata"] is null)
            updatedCell["metadata"] = new JsonObject();

        if (string.Equals(cellType, "code", StringComparison.Ordinal))
        {
            updatedCell["outputs"] = new JsonArray();
            updatedCell["execution_count"] = null;
        }
        else
        {
            updatedCell.Remove("outputs");
            updatedCell.Remove("execution_count");
        }

        return updatedCell;
    }

    private static string ToPastTense(string action) =>
        action switch
        {
            "insert" => "Edited",
            "replace" => "Edited",
            "delete" => "Edited",
            _ => "Edited",
        };
}
