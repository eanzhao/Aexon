namespace Aexon.Core.Mcp;

/// <summary>
/// Defines the contract for mcp connection.
/// </summary>
public interface IMcpConnection
{
    string ServerId { get; }
    Uri? Endpoint { get; }
    McpConnectionState State { get; }
    IReadOnlyList<McpToolDescriptor> Tools { get; }
    Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default);
    Task<McpReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents mcp connection.
/// </summary>
public sealed class McpConnection : IMcpConnection
{
    private readonly List<McpToolDescriptor> _tools = [];
    private IMcpClientSession? _session;
    private IReadOnlyList<McpResourceDescriptor>? _cachedResources;

    public McpConnection(
        string serverId,
        Uri? endpoint = null,
        McpConnectionState state = McpConnectionState.Pending,
        IEnumerable<McpToolDescriptor>? tools = null)
    {
        ServerId = serverId;
        Endpoint = endpoint;
        State = state;

        if (tools != null)
            _tools.AddRange(tools);
    }

    public string ServerId { get; }
    public Uri? Endpoint { get; }
    public McpConnectionState State { get; private set; }
    public IReadOnlyList<McpToolDescriptor> Tools => _tools;
    public DateTimeOffset LastUpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public void AttachSession(IMcpClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        InvalidateResourceCache();
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public void UpdateState(McpConnectionState state)
    {
        State = state;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ReplaceTools(IEnumerable<McpToolDescriptor> tools)
    {
        _tools.Clear();
        _tools.AddRange(tools);
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var session = EnsureReadySession();
        if (!refresh && _cachedResources != null)
            return _cachedResources;

        var resources = await session.ListResourcesAsync(cancellationToken);
        _cachedResources = resources;
        LastUpdatedAt = DateTimeOffset.UtcNow;
        return resources;
    }

    public Task<McpReadResourceResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default) =>
        EnsureReadySession().ReadResourceAsync(uri, cancellationToken);

    private IMcpClientSession EnsureReadySession()
    {
        if (State != McpConnectionState.Connected || _session == null)
        {
            throw new InvalidOperationException(
                $"MCP server {ServerId} is not connected.");
        }

        return _session;
    }

    private void InvalidateResourceCache() =>
        _cachedResources = null;
}
