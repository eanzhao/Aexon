using System.Text.Json;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Provides file write tool.
/// </summary>
public class FileWriteTool : ITool
{
    public string Name => "Write";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Create or overwrite a file with specified content.");

    public JsonElement GetInputSchema()
    {
        var schema = """
        {
            "type": "object",
            "properties": {
                "file_path": {
                    "type": "string",
                    "description": "Absolute path to the file to write"
                },
                "content": {
                    "type": "string",
                    "description": "Content to write to the file"
                }
            },
            "required": ["file_path", "content"],
            "additionalProperties": false
        }
        """;
        return JsonDocument.Parse(schema).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            Write content to a file. Creates the file and parent directories if they don't exist.
            If the file already exists, it will be overwritten.

            Usage:
            - Use an absolute file path
            - For modifying existing files, prefer the Edit tool instead
            - Content should be the complete file content
            """);
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken ct = default)
    {
        var filePath = input.GetProperty("file_path").GetString();
        var content = input.GetProperty("content").GetString();

        if (string.IsNullOrWhiteSpace(filePath))
            return ToolResult.Error("file_path is required.");

        filePath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(Path.Combine(context.WorkingDirectory, filePath));

        try
        {
            // Ensure that the target directory exists.
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var existed = File.Exists(filePath);
            await File.WriteAllTextAsync(filePath, content ?? "", ct);

            var lineCount = (content ?? "").Split('\n').Length;
            return ToolResult.Success(
                $"{(existed ? "Updated" : "Created")} file {filePath} ({lineCount} lines)");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to write file: {ex.Message}");
        }
    }

    public Task<PermissionResult> CheckPermissionsAsync(
        JsonElement input, ToolExecutionContext context)
    {
        var filePath = input.TryGetProperty("file_path", out var fp)
            ? fp.GetString() ?? ""
            : "";
        var resolvedPath = ResolvePath(filePath, context.WorkingDirectory);
        return Task.FromResult(PermissionResult.Ask($"Allow writing to: {resolvedPath}"));
    }

    public bool IsDestructive(JsonElement input) =>
        input.TryGetProperty("file_path", out var fp) &&
        File.Exists(ResolvePath(fp.GetString() ?? "", Environment.CurrentDirectory));

    public string? GetActivityDescription(JsonElement? input)
    {
        if (input?.TryGetProperty("file_path", out var fp) == true)
            return $"Writing {Path.GetFileName(fp.GetString() ?? "")}";
        return "Writing file";
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
