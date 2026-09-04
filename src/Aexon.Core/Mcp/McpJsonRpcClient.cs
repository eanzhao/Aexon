using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aexon.Core.Mcp;

/// <summary>
/// Defines the contract for a line-based MCP transport.
/// </summary>
public interface IMcpLineTransport : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an MCP protocol error.
/// </summary>
public sealed class McpProtocolException : Exception
{
    public McpProtocolException(
        string method,
        int? code,
        string message,
        JsonElement? data = null)
        : base($"MCP {method} failed: {message}")
    {
        Method = method;
        Code = code;
        Data = data;
    }

    public string Method { get; }
    public int? Code { get; }
    public new JsonElement? Data { get; }
}

/// <summary>
/// Sends JSON-RPC requests over an MCP line transport.
/// </summary>
public sealed class McpJsonRpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMcpLineTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextRequestId;

    public McpJsonRpcClient(IMcpLineTransport transport)
    {
        _transport = transport;
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var requestId = Interlocked.Increment(ref _nextRequestId).ToString();
            await WritePayloadAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters,
                },
                cancellationToken);

            while (true)
            {
                var message = await ReadMessageAsync(cancellationToken)
                    ?? throw new IOException($"MCP transport closed while waiting for {method}.");

                if (await HandleServerRequestAsync(message, cancellationToken))
                    continue;

                if (!MatchesId(message, requestId))
                    continue;

                if (TryGetProperty(message, "error", out var error))
                    throw CreateProtocolException(method, error);

                if (!TryGetProperty(message, "result", out var result))
                    return JsonSerializer.SerializeToElement(new { });

                return result.Clone();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendNotificationAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WritePayloadAsync(
                new
                {
                    jsonrpc = "2.0",
                    method,
                    @params = parameters,
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var cursor = (string?)null;
        var resources = new List<McpResourceDescriptor>();

        do
        {
            var result = await SendRequestAsync(
                "resources/list",
                string.IsNullOrWhiteSpace(cursor) ? null : new { cursor },
                cancellationToken);

            if (TryGetProperty(result, "resources", out var resourcesElement) &&
                resourcesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var resource in resourcesElement.EnumerateArray())
                    resources.Add(ParseResourceDescriptor(resource));
            }

            cursor = TryGetString(result, "nextCursor");
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return resources;
    }

    public async Task<McpReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("uri is required.", nameof(uri));

        var result = await SendRequestAsync(
            "resources/read",
            new { uri },
            cancellationToken);

        return ParseReadResourceResult(result);
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _transport.DisposeAsync();
    }

    private async Task<bool> HandleServerRequestAsync(
        JsonElement message,
        CancellationToken cancellationToken)
    {
        if (!TryGetProperty(message, "method", out var methodProperty) ||
            methodProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!TryGetProperty(message, "id", out var requestId))
            return true;

        await WritePayloadAsync(
            new
            {
                jsonrpc = "2.0",
                id = DeserializeUntyped(requestId),
                error = new
                {
                    code = -32601,
                    message = "Aexon does not support server-initiated MCP requests.",
                },
            },
            cancellationToken);

        return true;
    }

    private async Task WritePayloadAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        await _transport.WriteLineAsync(json, cancellationToken);
    }

    private async Task<JsonElement?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _transport.ReadLineAsync(cancellationToken);
            if (line == null)
                return null;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                throw new IOException($"Invalid MCP JSON message: {ex.Message}");
            }
        }
    }

    private static bool MatchesId(JsonElement message, string requestId) =>
        TryGetProperty(message, "id", out var responseId) &&
        string.Equals(NormalizeId(responseId), requestId, StringComparison.Ordinal);

    private static McpResourceDescriptor ParseResourceDescriptor(JsonElement resource)
    {
        if (!TryGetProperty(resource, "uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("MCP resource is missing uri.");
        }

        if (!TryGetProperty(resource, "name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("MCP resource is missing name.");
        }

        return new McpResourceDescriptor(
            uriElement.GetString() ?? string.Empty,
            nameElement.GetString() ?? string.Empty,
            TryGetString(resource, "description"),
            TryGetString(resource, "mimeType"));
    }

    private static McpReadResourceResult ParseReadResourceResult(JsonElement result)
    {
        if (!TryGetProperty(result, "contents", out var contentsElement) ||
            contentsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("MCP resource read response is missing contents.");
        }

        var contents = new List<McpResourceContent>();
        foreach (var content in contentsElement.EnumerateArray())
        {
            if (!TryGetProperty(content, "uri", out var uriElement) ||
                uriElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("MCP resource content is missing uri.");
            }

            contents.Add(new McpResourceContent(
                uriElement.GetString() ?? string.Empty,
                TryGetString(content, "mimeType"),
                TryGetString(content, "text"),
                TryGetString(content, "blob")));
        }

        return new McpReadResourceResult(contents);
    }

    private static string? NormalizeId(JsonElement id) =>
        id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };

    private static string? TryGetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static McpProtocolException CreateProtocolException(
        string method,
        JsonElement error)
    {
        int? code = TryGetProperty(error, "code", out var codeProperty) &&
                    codeProperty.ValueKind == JsonValueKind.Number &&
                    codeProperty.TryGetInt32(out var parsedCode)
            ? parsedCode
            : null;

        var message = TryGetProperty(error, "message", out var messageProperty) &&
                      messageProperty.ValueKind == JsonValueKind.String
            ? messageProperty.GetString() ?? "Unknown MCP error"
            : "Unknown MCP error";

        JsonElement? data = TryGetProperty(error, "data", out var dataProperty)
            ? dataProperty.Clone()
            : null;

        return new McpProtocolException(method, code, message, data);
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
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

    private static object? DeserializeUntyped(JsonElement value)
    {
        using var document = JsonDocument.Parse(value.GetRawText());
        return JsonSerializer.Deserialize<object>(document.RootElement.GetRawText(), SerializerOptions);
    }
}
