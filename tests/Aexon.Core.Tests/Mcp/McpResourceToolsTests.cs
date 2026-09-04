using System.Text.Json;
using Aexon.Core.Mcp;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;
using Aexon.Tools;

namespace Aexon.Core.Tests.Mcp;

/// <summary>
/// Contains tests for MCP resource tools.
/// </summary>
public sealed class McpResourceToolsTests
{
    [Fact]
    public async Task ListMcpResourcesTool_ExecuteAsync_AggregatesConnectedServers()
    {
        var manager = new McpConnectionManager();
        var alpha = RegisterConnection(
            manager,
            "alpha",
            new FakeSession(
                resources: [new McpResourceDescriptor("file:///alpha.txt", "Alpha", "first", "text/plain")]));
        var beta = RegisterConnection(
            manager,
            "beta",
            new FakeSession(
                resources: [new McpResourceDescriptor("file:///beta.txt", "Beta", null, "text/plain")]));
        var tool = new ListMcpResourcesTool(manager);

        var result = await tool.ExecuteAsync(Json(new { }), CreateContext());

        Assert.False(result.IsError);
        using var document = JsonDocument.Parse(result.Data);
        Assert.Equal(2, document.RootElement.GetProperty("serverCount").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("resourceCount").GetInt32());
        var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();
        Assert.Contains(resources, item => item.GetProperty("server").GetString() == "alpha");
        Assert.Contains(resources, item => item.GetProperty("server").GetString() == "beta");
        Assert.Equal(1, alpha.ListResourcesCallCount);
        Assert.Equal(1, beta.ListResourcesCallCount);
    }

    [Fact]
    public async Task ListMcpResourcesTool_ExecuteAsync_ReturnsErrorsForUnknownAndDisconnectedServers()
    {
        var manager = new McpConnectionManager();
        manager.Register(new McpConnection("offline", state: McpConnectionState.Failed));
        var tool = new ListMcpResourcesTool(manager);

        var unknown = await tool.ExecuteAsync(Json(new { server = "missing" }), CreateContext());
        var disconnected = await tool.ExecuteAsync(Json(new { server = "offline" }), CreateContext());

        Assert.True(unknown.IsError);
        Assert.Equal("Unknown MCP server: missing.", unknown.Data);
        Assert.True(disconnected.IsError);
        Assert.Equal("MCP server offline is not connected.", disconnected.Data);
    }

    [Fact]
    public async Task ReadMcpResourceTool_ExecuteAsync_ReturnsTextAndBlobPayloads()
    {
        var manager = new McpConnectionManager();
        RegisterConnection(
            manager,
            "alpha",
            new FakeSession(
                readResult: new McpReadResourceResult(
                [
                    new McpResourceContent("file:///alpha.txt", "text/plain", "hello", null),
                    new McpResourceContent(
                        "file:///asset.bin",
                        "application/octet-stream",
                        null,
                        new string('A', 4100)),
                ])));
        var tool = new ReadMcpResourceTool(manager);

        var result = await tool.ExecuteAsync(
            Json(new { server = "alpha", uri = "file:///alpha.txt" }),
            CreateContext());

        Assert.False(result.IsError);
        using var document = JsonDocument.Parse(result.Data);
        var contents = document.RootElement.GetProperty("contents").EnumerateArray().ToArray();
        Assert.Equal("text", contents[0].GetProperty("kind").GetString());
        Assert.Equal("hello", contents[0].GetProperty("text").GetString());
        Assert.Equal("blob", contents[1].GetProperty("kind").GetString());
        Assert.True(contents[1].GetProperty("isTruncated").GetBoolean());
        Assert.Equal(4100, contents[1].GetProperty("totalBase64Length").GetInt32());
        Assert.Equal(4096, contents[1].GetProperty("base64Preview").GetString()!.Length);
    }

    [Fact]
    public async Task ReadMcpResourceTool_ExecuteAsync_ReturnsErrorsForUnknownServerAndServerFailures()
    {
        var manager = new McpConnectionManager();
        RegisterConnection(
            manager,
            "alpha",
            new FakeSession(readException: new McpProtocolException("resources/read", -32001, "unknown resource")));
        var tool = new ReadMcpResourceTool(manager);

        var unknown = await tool.ExecuteAsync(
            Json(new { server = "missing", uri = "file:///alpha.txt" }),
            CreateContext());
        var failure = await tool.ExecuteAsync(
            Json(new { server = "alpha", uri = "file:///missing.txt" }),
            CreateContext());

        Assert.True(unknown.IsError);
        Assert.Equal("Unknown MCP server: missing.", unknown.Data);
        Assert.True(failure.IsError);
        Assert.Contains("Failed to read MCP resource file:///missing.txt from server alpha", failure.Data, StringComparison.Ordinal);
        Assert.Contains("unknown resource", failure.Data, StringComparison.Ordinal);
    }

    private static FakeSession RegisterConnection(
        McpConnectionManager manager,
        string serverId,
        FakeSession session)
    {
        var connection = new McpConnection(serverId, state: McpConnectionState.Connected);
        connection.AttachSession(session);
        manager.Register(connection);
        return session;
    }

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value);

    private static ToolExecutionContext CreateContext() =>
        new()
        {
            WorkingDirectory = "/tmp",
            PermissionContext = new PermissionContext(),
            Tools = [],
            Messages = [],
            CancellationToken = CancellationToken.None,
        };

    private sealed class FakeSession : IMcpClientSession
    {
        private readonly IReadOnlyList<McpResourceDescriptor> _resources;
        private readonly McpReadResourceResult _readResult;
        private readonly Exception? _readException;

        public FakeSession(
            IReadOnlyList<McpResourceDescriptor>? resources = null,
            McpReadResourceResult? readResult = null,
            Exception? readException = null)
        {
            _resources = resources ?? [];
            _readResult = readResult ?? new McpReadResourceResult([]);
            _readException = readException;
        }

        public string ServerId => "server-a";

        public int ListResourcesCallCount { get; private set; }

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpToolDescriptor>>([]);

        public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken = default)
        {
            ListResourcesCallCount++;
            return Task.FromResult(_resources);
        }

        public Task<McpReadResourceResult> ReadResourceAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            if (_readException != null)
                return Task.FromException<McpReadResourceResult>(_readException);

            return Task.FromResult(_readResult);
        }

        public Task<McpCallToolResult> CallToolAsync(
            string toolName,
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpCallToolResult(string.Empty, false));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
