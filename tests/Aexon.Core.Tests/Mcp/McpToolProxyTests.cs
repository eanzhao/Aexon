using System.Text.Json;
using Aexon.Core.Mcp;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;

namespace Aexon.Core.Tests.Mcp;

/// <summary>
/// Contains tests for the MCP tool proxy.
/// </summary>
public sealed class McpToolProxyTests
{
    [Fact]
    public async Task Proxy_UsesDescriptorMetadataForDescriptionAndExecution()
    {
        var session = new FakeSession(new McpCallToolResult("remote ok", false));
        var descriptor = new McpToolDescriptor
        {
            Name = "alpha",
            Description = "Remote alpha tool.",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            ReadOnlyHint = true,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Alpha Tool",
            },
        };
        var proxy = new McpToolProxy("mcp_server-a_alpha", "server-a", "alpha", descriptor, session);

        var description = await proxy.GetDescriptionAsync();
        var result = await proxy.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { value = 1 }),
            CreateContext());
        var permission = await proxy.CheckPermissionsAsync(JsonSerializer.SerializeToElement(new { }), CreateContext());

        Assert.Contains("Title: Alpha Tool.", description, StringComparison.Ordinal);
        Assert.Contains("This tool is marked read-only.", description, StringComparison.Ordinal);
        Assert.Contains("configured server boundary", description, StringComparison.Ordinal);
        Assert.False(result.IsError);
        Assert.Equal("remote ok", result.Data);
        Assert.Single(session.Calls);
        Assert.Equal("alpha", session.Calls[0].ToolName);
        Assert.Equal(PermissionBehavior.Allow, permission.Behavior);
        Assert.True(proxy.IsReadOnly(default));
        Assert.False(proxy.IsConcurrencySafe(default));
        Assert.Equal("alpha (MCP:server-a)", proxy.GetUserFacingName());
        Assert.Equal("Calling alpha on MCP server server-a", proxy.GetActivityDescription(null));
    }

    [Fact]
    public async Task Proxy_AsksForPermissionWhenDescriptorCanWriteOrReachOpenWorld()
    {
        var descriptor = new McpToolDescriptor
        {
            Name = "beta",
            Description = "Remote beta tool.",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            ReadOnlyHint = false,
            OpenWorldHint = true,
            AlwaysLoad = true,
            SearchHint = "beta",
        };
        var proxy = new McpToolProxy("mcp_server-a_beta", "server-a", "beta", descriptor, new FakeSession(new McpCallToolResult("boom", true)));

        var permission = await proxy.CheckPermissionsAsync(JsonSerializer.SerializeToElement(new { }), CreateContext());
        var result = await proxy.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { value = 2 }),
            CreateContext());
        var schema = proxy.GetInputSchema();

        Assert.Equal(PermissionBehavior.Ask, permission.Behavior);
        Assert.Contains("Allow beta (MCP:server-a)?", permission.Message, StringComparison.Ordinal);
        Assert.True(result.IsError);
        Assert.Equal("boom", result.Data);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(descriptor.OpenWorldHint);
        Assert.True(descriptor.AlwaysLoad);
        Assert.Equal("beta", descriptor.SearchHint);
        Assert.Empty(descriptor.Metadata);
    }

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
        private readonly McpCallToolResult _result;

        public FakeSession(McpCallToolResult result)
        {
            _result = result;
        }

        public string ServerId => "server-a";

        public List<(string ToolName, JsonElement Arguments)> Calls { get; } = [];

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpToolDescriptor>>([]);

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
