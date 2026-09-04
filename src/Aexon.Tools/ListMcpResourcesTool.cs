using System.Text.Json;
using System.Text.Json.Serialization;
using Aexon.Core.Mcp;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;

namespace Aexon.Tools;

public sealed class ListMcpResourcesToolInput
{
    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("refresh")]
    public bool Refresh { get; set; }
}

/// <summary>
/// Lists resources exposed by connected MCP servers.
/// </summary>
public sealed class ListMcpResourcesTool : ITool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly McpConnectionManager _connectionManager;

    public ListMcpResourcesTool(McpConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public string Name => "ListMcpResources";

    public string[] Aliases => ["ListMcpResourcesTool"];

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult(
            "List resources exposed by MCP servers. Use this before ReadMcpResource to discover valid resource URIs.");

    public JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Optional MCP server id. Omit to aggregate resources from all connected MCP servers."
            },
            "refresh": {
              "type": "boolean",
              "description": "When true, bypass the in-memory cache and refresh resources from the server."
            }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            List resources available from connected MCP servers.

            Usage:
            - Omit server to aggregate resources from every connected MCP server
            - Pass server when you need resources from one specific MCP server
            - Use refresh=true if a server's resources may have changed since the last list call
            - ReadMcpResource expects a server id and a URI returned by this tool
            """);
    }

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ListMcpResourcesToolInput>(input, JsonOptions);
            if (parsed?.Server != null && string.IsNullOrWhiteSpace(parsed.Server))
                return Task.FromResult(ValidationResult.Invalid("server must not be blank when provided."));

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
        if (input?.TryGetProperty("server", out var serverProperty) == true)
        {
            var server = serverProperty.GetString();
            if (!string.IsNullOrWhiteSpace(server))
                return $"Listing MCP resources from {server}";
        }

        return "Listing MCP resources";
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ListMcpResourcesToolInput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ListMcpResourcesToolInput>(input, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        var serverId = parsed?.Server?.Trim();
        var refresh = parsed?.Refresh ?? false;

        if (!string.IsNullOrWhiteSpace(serverId))
        {
            if (!_connectionManager.TryGet(serverId, out var connection) || connection == null)
                return ToolResult.Error($"Unknown MCP server: {serverId}.");

            if (connection.State != McpConnectionState.Connected)
                return ToolResult.Error($"MCP server {serverId} is not connected.");

            return await BuildResultAsync([connection], refresh, cancellationToken);
        }

        var connections = _connectionManager
            .Snapshot()
            .Where(snapshot => snapshot.State == McpConnectionState.Connected)
            .Select(snapshot =>
            {
                _connectionManager.TryGet(snapshot.ServerId, out var connection);
                return connection;
            })
            .OfType<McpConnection>()
            .ToArray();

        return await BuildResultAsync(connections, refresh, cancellationToken);
    }

    private static async Task<ToolResult> BuildResultAsync(
        IReadOnlyList<McpConnection> connections,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (connections.Count == 0)
        {
            return ToolResult.Success(JsonSerializer.Serialize(
                new
                {
                    serverCount = 0,
                    resourceCount = 0,
                    refreshed = refresh,
                    resources = Array.Empty<object>(),
                    note = "No connected MCP servers.",
                },
                JsonOptions));
        }

        var resources = new List<object>();
        foreach (var connection in connections.OrderBy(item => item.ServerId, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<McpResourceDescriptor> descriptors;
            try
            {
                descriptors = await connection.ListResourcesAsync(refresh, cancellationToken);
            }
            catch (Exception ex)
            {
                return ToolResult.Error(
                    $"Failed to list resources from MCP server {connection.ServerId}: {ex.Message}");
            }

            resources.AddRange(descriptors.Select(resource => new
            {
                server = connection.ServerId,
                uri = resource.Uri,
                name = resource.Name,
                description = resource.Description,
                mimeType = resource.MimeType,
            }));
        }

        var payload = new
        {
            serverCount = connections.Count,
            resourceCount = resources.Count,
            refreshed = refresh,
            resources,
        };

        return ToolResult.Success(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
