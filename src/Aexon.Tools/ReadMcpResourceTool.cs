using System.Text.Json;
using System.Text.Json.Serialization;
using Aexon.Core.Mcp;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;

namespace Aexon.Tools;

public sealed class ReadMcpResourceToolInput
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
}

/// <summary>
/// Reads a specific resource exposed by an MCP server.
/// </summary>
public sealed class ReadMcpResourceTool : ITool
{
    private const int BlobPreviewLength = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly McpConnectionManager _connectionManager;

    public ReadMcpResourceTool(McpConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public string Name => "ReadMcpResource";

    public string[] Aliases => ["ReadMcpResourceTool"];

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult(
            "Read the contents of a specific MCP resource by server id and URI.");

    public JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "The MCP server id that exposes the resource"
            },
            "uri": {
              "type": "string",
              "description": "The exact resource URI to read"
            }
          },
          "required": ["server", "uri"],
          "additionalProperties": false
        }
        """).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            Read a specific MCP resource.

            Usage:
            - Pass the exact server id and URI you want to read
            - Prefer calling ListMcpResources first to discover valid URIs
            - Text resources are returned as text
            - Binary resources are returned as truncated base64 previews to keep tool output bounded
            """);
    }

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ReadMcpResourceToolInput>(input, JsonOptions);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Server))
                return Task.FromResult(ValidationResult.Invalid("server is required."));

            if (string.IsNullOrWhiteSpace(parsed.Uri))
                return Task.FromResult(ValidationResult.Invalid("uri is required."));

            return Task.FromResult(ValidationResult.Valid());
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ValidationResult.Invalid(ex.Message));
        }
    }

    public Task<PermissionResult> CheckPermissionsAsync(JsonElement input, ToolExecutionContext context) =>
        Task.FromResult(PermissionResult.Allow());

    public bool IsReadOnly(JsonElement input) => true;

    public bool IsConcurrencySafe(JsonElement input) => true;

    public string? GetActivityDescription(JsonElement? input)
    {
        if (input?.TryGetProperty("uri", out var uriProperty) == true)
        {
            var uri = uriProperty.GetString();
            if (!string.IsNullOrWhiteSpace(uri))
                return $"Reading MCP resource {uri}";
        }

        return "Reading MCP resource";
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReadMcpResourceToolInput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReadMcpResourceToolInput>(input, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Server))
            return ToolResult.Error("server is required.");

        if (string.IsNullOrWhiteSpace(parsed.Uri))
            return ToolResult.Error("uri is required.");

        var serverId = parsed.Server.Trim();
        var uri = parsed.Uri.Trim();

        if (!_connectionManager.TryGet(serverId, out var connection) || connection == null)
            return ToolResult.Error($"Unknown MCP server: {serverId}.");

        if (connection.State != McpConnectionState.Connected)
            return ToolResult.Error($"MCP server {serverId} is not connected.");

        McpReadResourceResult result;
        try
        {
            result = await connection.ReadResourceAsync(uri, cancellationToken);
        }
        catch (Exception ex)
        {
            return ToolResult.Error(
                $"Failed to read MCP resource {uri} from server {serverId}: {ex.Message}");
        }

        var payload = new
        {
            server = serverId,
            uri,
            contents = result.Contents.Select(SerializeContent).ToArray(),
        };

        return ToolResult.Success(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static object SerializeContent(McpResourceContent content)
    {
        if (content.Text != null)
        {
            return new
            {
                uri = content.Uri,
                mimeType = content.MimeType,
                kind = "text",
                text = content.Text,
            };
        }

        var blob = content.Blob ?? string.Empty;
        var preview = blob.Length > BlobPreviewLength
            ? blob[..BlobPreviewLength]
            : blob;

        return new
        {
            uri = content.Uri,
            mimeType = content.MimeType,
            kind = "blob",
            note = "Binary resource content is returned as a truncated base64 preview to keep tool output bounded.",
            base64Preview = preview,
            isTruncated = blob.Length > BlobPreviewLength,
            totalBase64Length = blob.Length,
        };
    }
}
