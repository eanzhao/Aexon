using System.Text.Json;
using Aexon.Core.Mcp;
using Aexon.Core.Permissions;
using Aexon.Core.Tests.Runtime;
using Aexon.Core.Tools;

namespace Aexon.Core.Tests.Mcp;

/// <summary>
/// Contains tests for the MCP runtime integration.
/// </summary>
public sealed class McpRuntimeTests
{
    [Fact]
    public async Task CreateAsync_RegistersRemoteToolsAndRoutesExecutionThroughProxy()
    {
        using var temp = new TempDirectory();
        var configPath = temp.WriteFile("settings.json", """
{
  "mcpServers": {
    "server-a": {
      "command": "dummy-server"
    }
  }
}
""");

        var descriptor = new McpToolDescriptor
        {
            Name = "alpha",
            Description = "remote alpha tool",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            ReadOnlyHint = true,
        };

        var session = new FakeSession(
            "server-a",
            [descriptor],
            new McpCallToolResult("remote ok", false));
        var factory = new FakeSessionFactory(session);
        var registry = new ToolRegistry();

        await using var runtime = await McpRuntime.CreateAsync(
            registry,
            temp.Root,
            configPath,
            factory);

        var snapshot = Assert.Single(runtime.ConnectionManager.Snapshot());
        Assert.Equal("server-a", snapshot.ServerId);
        Assert.Equal(McpConnectionState.Connected, snapshot.State);
        Assert.Equal(1, snapshot.ToolCount);
        Assert.Contains(
            runtime.StartupMessages,
            message => message.Contains("connected (1 tools)", StringComparison.Ordinal));

        Assert.True(runtime.DynamicTools.TryGet("server-a", "alpha", out var registeredTool));
        Assert.NotNull(registeredTool);

        var proxy = Assert.Single(registry.GetAllTools().OfType<McpToolProxy>());
        Assert.Equal("mcp_server-a_alpha", proxy.Name);
        Assert.True(proxy.IsReadOnly(JsonSerializer.SerializeToElement(new { })));

        var result = await proxy.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { value = 1 }),
            new ToolExecutionContext
            {
                WorkingDirectory = temp.Root,
                PermissionContext = new PermissionContext(),
                Tools = registry.GetAllTools(),
                Messages = [],
                CancellationToken = CancellationToken.None,
            });

        Assert.Equal("remote ok", result.Data);
        Assert.False(result.IsError);
        Assert.Single(session.Calls);
        Assert.Equal("alpha", session.Calls[0].ToolName);
        Assert.Equal("""{"value":1}""", session.Calls[0].Arguments.GetRawText());
    }

    private sealed class FakeSessionFactory : IMcpClientSessionFactory
    {
        private readonly FakeSession _session;

        public FakeSessionFactory(FakeSession session)
        {
            _session = session;
        }

        public Task<IMcpClientSession> ConnectAsync(
            McpServerConfig config,
            string fallbackWorkingDirectory,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_session.ServerId, config.ServerId);
            return Task.FromResult<IMcpClientSession>(_session);
        }
    }

    private sealed class FakeSession : IMcpClientSession
    {
        private readonly IReadOnlyList<McpToolDescriptor> _tools;
        private readonly McpCallToolResult _result;

        public FakeSession(
            string serverId,
            IReadOnlyList<McpToolDescriptor> tools,
            McpCallToolResult result)
        {
            ServerId = serverId;
            _tools = tools;
            _result = result;
        }

        public string ServerId { get; }

        public List<(string ToolName, JsonElement Arguments)> Calls { get; } = [];

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_tools);

        public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpResourceDescriptor>>([]);

        public Task<McpReadResourceResult> ReadResourceAsync(
            string uri,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpReadResourceResult([]));

        public Task<McpCallToolResult> CallToolAsync(
            string toolName,
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((toolName, arguments.Clone()));
            return Task.FromResult(_result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
