using System.Text.Json;

namespace Aexon.Core.Mcp;

/// <summary>
/// Represents the normalized result of an MCP tool call.
/// </summary>
public sealed record McpCallToolResult(
    string Text,
    bool IsError);

/// <summary>
/// Defines the contract for an MCP client session.
/// </summary>
public interface IMcpClientSession : IAsyncDisposable
{
    string ServerId { get; }

    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken = default);

    Task<McpReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default);

    Task<McpCallToolResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines the contract for creating MCP client sessions.
/// </summary>
public interface IMcpClientSessionFactory
{
    Task<IMcpClientSession> ConnectAsync(
        McpServerConfig config,
        string fallbackWorkingDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates stdio-backed MCP client sessions.
/// </summary>
public sealed class McpClientSessionFactory : IMcpClientSessionFactory
{
    private readonly Action<string>? _stderrSink;

    public McpClientSessionFactory(Action<string>? stderrSink = null)
    {
        _stderrSink = stderrSink;
    }

    public async Task<IMcpClientSession> ConnectAsync(
        McpServerConfig config,
        string fallbackWorkingDirectory,
        CancellationToken cancellationToken = default)
    {
        return await McpClientSession.ConnectAsync(
            config,
            fallbackWorkingDirectory,
            _stderrSink,
            cancellationToken);
    }
}

/// <summary>
/// Connects to an MCP server and exposes tools over JSON-RPC.
/// </summary>
public sealed class McpClientSession : IMcpClientSession
{
    private const string ProtocolVersion = "2025-03-26";

    private readonly McpJsonRpcClient _client;

    private McpClientSession(
        string serverId,
        McpJsonRpcClient client)
    {
        ServerId = serverId;
        _client = client;
    }

    public string ServerId { get; }

    public static async Task<McpClientSession> ConnectAsync(
        McpServerConfig config,
        string fallbackWorkingDirectory,
        Action<string>? stderrSink = null,
        CancellationToken cancellationToken = default)
    {
        var transport = await McpStdioTransport.StartAsync(
            config,
            fallbackWorkingDirectory,
            stderrSink,
            cancellationToken);
        var client = new McpJsonRpcClient(transport);
        var session = new McpClientSession(config.ServerId, client);

        try
        {
            await session.InitializeAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var cursor = (string?)null;
        var tools = new List<McpToolDescriptor>();

        do
        {
            var result = await _client.SendRequestAsync(
                "tools/list",
                string.IsNullOrWhiteSpace(cursor) ? null : new { cursor },
                cancellationToken);

            if (TryGetProperty(result, "tools", out var toolsElement) &&
                toolsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in toolsElement.EnumerateArray())
                    tools.Add(ParseToolDescriptor(tool));
            }

            cursor = TryGetString(result, "nextCursor");
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return tools;
    }

    public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
        _client.ListResourcesAsync(cancellationToken);

    public Task<McpReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default) =>
        _client.ReadResourceAsync(uri, cancellationToken);

    public async Task<McpCallToolResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.SendRequestAsync(
            "tools/call",
            new
            {
                name = toolName,
                arguments,
            },
            cancellationToken);

        return ParseToolCallResult(result);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _client.SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new
                {
                    name = "Aexon",
                    version = GetClientVersion(),
                },
            },
            cancellationToken);

        await _client.SendNotificationAsync(
            "notifications/initialized",
            new { },
            cancellationToken);
    }

    private static McpToolDescriptor ParseToolDescriptor(JsonElement tool)
    {
        if (!TryGetProperty(tool, "name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("MCP tool is missing name.");
        }

        var annotations = TryGetProperty(tool, "annotations", out var annotationsElement)
            ? annotationsElement
            : default;

        var description = TryGetString(tool, "description") ?? string.Empty;
        var title = TryGetString(tool, "title") ?? TryGetString(annotations, "title");
        var inputSchema = TryGetProperty(tool, "inputSchema", out var inputSchemaElement) ||
                          TryGetProperty(tool, "input_schema", out inputSchemaElement)
            ? inputSchemaElement.Clone()
            : JsonSerializer.SerializeToElement(new { type = "object" });

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(title))
            metadata["title"] = title;

        return new McpToolDescriptor
        {
            Name = nameElement.GetString() ?? string.Empty,
            Description = description,
            InputSchema = inputSchema,
            ReadOnlyHint = TryGetBool(annotations, "readOnlyHint"),
            OpenWorldHint = TryGetBool(annotations, "openWorldHint"),
            AlwaysLoad = false,
            SearchHint = title,
            Metadata = metadata,
        };
    }

    private static McpCallToolResult ParseToolCallResult(JsonElement result)
    {
        var parts = new List<string>();

        if (TryGetProperty(result, "content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = TryGetString(block, "type");
                switch (type)
                {
                    case "text":
                        {
                            var text = TryGetString(block, "text");
                            if (!string.IsNullOrWhiteSpace(text))
                                parts.Add(text);
                            break;
                        }
                    case "image":
                        {
                            var mimeType = TryGetString(block, "mimeType") ?? "image";
                            parts.Add($"[MCP image result: {mimeType}]");
                            break;
                        }
                    case "audio":
                        {
                            var mimeType = TryGetString(block, "mimeType") ?? "audio";
                            parts.Add($"[MCP audio result: {mimeType}]");
                            break;
                        }
                    default:
                        parts.Add(block.GetRawText());
                        break;
                }
            }
        }

        if (TryGetProperty(result, "structuredContent", out var structuredContent) &&
            structuredContent.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            var serialized = structuredContent.GetRawText();
            if (parts.Count == 0)
                parts.Add(serialized);
            else
                parts.Add($"Structured content:\n{serialized}");
        }

        if (parts.Count == 0)
            parts.Add(result.GetRawText());

        return new McpCallToolResult(
            string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part))),
            TryGetBool(result, "isError"));
    }

    private static string GetClientVersion() =>
        typeof(McpClientSession).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            property = default;
            return false;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetBool(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();
}
