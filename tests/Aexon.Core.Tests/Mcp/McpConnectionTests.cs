using Aexon.Core.Mcp;

namespace Aexon.Core.Tests.Mcp;

/// <summary>
/// Contains tests for MCP connection resource access.
/// </summary>
public sealed class McpConnectionTests
{
    [Fact]
    public async Task ListResourcesAsync_CachesResultsUntilRefreshAndReconnect()
    {
        var firstSession = new FakeSession(
            [new McpResourceDescriptor("file:///alpha.txt", "Alpha", null, "text/plain")]);
        var secondSession = new FakeSession(
            [new McpResourceDescriptor("file:///beta.txt", "Beta", null, "text/plain")]);
        var connection = new McpConnection("server-a", state: McpConnectionState.Connected);
        connection.AttachSession(firstSession);

        var first = await connection.ListResourcesAsync();
        var cached = await connection.ListResourcesAsync();

        Assert.Equal(1, firstSession.ListResourcesCallCount);
        Assert.Same(first, cached);

        var refreshed = await connection.ListResourcesAsync(refresh: true);

        Assert.Equal(2, firstSession.ListResourcesCallCount);
        Assert.Equal("file:///alpha.txt", refreshed[0].Uri);

        connection.AttachSession(secondSession);
        var afterReconnect = await connection.ListResourcesAsync();

        Assert.Equal(1, secondSession.ListResourcesCallCount);
        Assert.Equal("file:///beta.txt", afterReconnect[0].Uri);
    }

    private sealed class FakeSession : IMcpClientSession
    {
        private readonly IReadOnlyList<McpResourceDescriptor> _resources;

        public FakeSession(IReadOnlyList<McpResourceDescriptor> resources)
        {
            _resources = resources;
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpReadResourceResult([]));

        public Task<McpCallToolResult> CallToolAsync(
            string toolName,
            System.Text.Json.JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpCallToolResult(string.Empty, false));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
