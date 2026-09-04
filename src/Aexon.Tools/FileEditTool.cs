using System.Text;
using System.Text.Json;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Provides file edit tool.
/// </summary>
public class FileEditTool : ITool
{
    public string Name => "Edit";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Edit a file by searching for exact text and replacing it.");

    public JsonElement GetInputSchema()
    {
        var schema = """
        {
            "type": "object",
            "properties": {
                "file_path": {
                    "type": "string",
                    "description": "Absolute path to the file to edit"
                },
                "old_string": {
                    "type": "string",
                    "description": "The exact text to search for (must match exactly)"
                },
                "new_string": {
                    "type": "string",
                    "description": "The text to replace old_string with"
                }
            },
            "required": ["file_path", "old_string", "new_string"],
            "additionalProperties": false
        }
        """;
        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            Edit a file by searching for an exact string and replacing it with new content.

            Rules:
            - old_string MUST match exactly (including whitespace and indentation)
            - old_string must be unique in the file (appear exactly once)
            - Both old_string and new_string should include enough context lines for unique matching
            - For creating new files, use the Write tool instead
            - For large rewrites, use the Write tool instead
            """);
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken ct = default)
    {
        var filePath = input.GetProperty("file_path").GetString();
        var oldString = input.GetProperty("old_string").GetString();
        var newString = input.GetProperty("new_string").GetString();

        if (string.IsNullOrWhiteSpace(filePath))
            return ToolResult.Error("file_path is required.");
        if (oldString == null)
            return ToolResult.Error("old_string is required.");
        if (oldString.Length == 0)
            return ToolResult.Error("old_string must not be empty.");
        if (newString == null)
            return ToolResult.Error("new_string is required.");

        filePath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(Path.Combine(context.WorkingDirectory, filePath));

        if (!File.Exists(filePath))
            return ToolResult.Error($"File not found: {filePath}");

        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);

            // Count exact matches before attempting the replacement.
            var count = CountOccurrences(content, oldString);
            if (count == 0)
                return ToolResult.Error(
                    $"old_string not found in {filePath}. Make sure it matches exactly, " +
                    "including whitespace and indentation.");

            if (count > 1)
                return ToolResult.Error(
                    $"old_string found {count} times in {filePath}. " +
                    "Include more context to make the match unique.");

            // Perform the replacement.
            var newContent = content.Replace(oldString, newString);
            await File.WriteAllTextAsync(filePath, newContent, ct);

            // Produce a short diff summary.
            var oldLines = oldString.Split('\n').Length;
            var newLines = newString.Split('\n').Length;
            var diffSummary = oldLines == newLines
                ? $"Modified {oldLines} line(s)"
                : $"Replaced {oldLines} line(s) with {newLines} line(s)";

            return ToolResult.Success($"Edited {filePath}: {diffSummary}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error editing file: {ex.Message}");
        }
    }

    public Task<PermissionResult> CheckPermissionsAsync(
        JsonElement input, ToolExecutionContext context)
    {
        var filePath = input.TryGetProperty("file_path", out var fp)
            ? fp.GetString() ?? ""
            : "";
        var resolvedPath = ResolvePath(filePath, context.WorkingDirectory);
        return Task.FromResult(PermissionResult.Ask($"Allow editing: {resolvedPath}"));
    }

    public string? GetActivityDescription(JsonElement? input)
    {
        if (input?.TryGetProperty("file_path", out var fp) == true)
            return $"Editing {Path.GetFileName(fp.GetString() ?? "")}";
        return "Editing file";
    }

    private static int CountOccurrences(string content, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = content.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ResolvePath(string path, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(workingDirectory, path));
    }
}
