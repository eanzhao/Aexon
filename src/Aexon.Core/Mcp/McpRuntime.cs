using System.Security.Cryptography;
using System.Text;
using Aexon.Core.Tools;

namespace Aexon.Core.Mcp;

/// <summary>
/// Connects configured MCP servers and registers their tools.
/// </summary>
public sealed class McpRuntime : IAsyncDisposable
{
    private readonly List<IMcpClientSession> _sessions = [];
    private readonly List<string> _startupMessages = [];

    public McpRuntime()
    {
        ConnectionManager = new McpConnectionManager();
        DynamicTools = new McpDynamicToolRegistry();
    }

    public McpConnectionManager ConnectionManager { get; }

    public McpDynamicToolRegistry DynamicTools { get; }

    public IReadOnlyList<string> StartupMessages => _startupMessages;

    public string? StartupSummary =>
        _startupMessages.Count == 0
            ? null
            : string.Join(Environment.NewLine, _startupMessages);

    public static async Task<McpRuntime> CreateAsync(
        ToolRegistry toolRegistry,
        string workingDirectory,
        string? explicitConfigPath = null,
        IMcpClientSessionFactory? sessionFactory = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = new McpRuntime();
        await runtime.LoadAsync(
            toolRegistry,
            workingDirectory,
            explicitConfigPath,
            sessionFactory ?? new McpClientSessionFactory(),
            cancellationToken);
        return runtime;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
            await session.DisposeAsync();
    }

    private async Task LoadAsync(
        ToolRegistry toolRegistry,
        string workingDirectory,
        string? explicitConfigPath,
        IMcpClientSessionFactory sessionFactory,
        CancellationToken cancellationToken)
    {
        var loadResult = McpSettingsLoader.Load(workingDirectory, explicitConfigPath);
        foreach (var diagnostic in loadResult.Diagnostics)
            _startupMessages.Add(diagnostic);

        foreach (var server in loadResult.Servers)
        {
            if (server.Disabled)
            {
                _startupMessages.Add($"MCP {server.ServerId}: disabled in settings.");
                continue;
            }

            var connection = new McpConnection(server.ServerId);
            ConnectionManager.Register(connection);

            try
            {
                var session = await sessionFactory.ConnectAsync(
                    server,
                    workingDirectory,
                    cancellationToken);
                _sessions.Add(session);
                connection.AttachSession(session);

                var tools = await session.ListToolsAsync(cancellationToken);
                ConnectionManager.UpdateState(server.ServerId, McpConnectionState.Connected);
                ConnectionManager.UpdateTools(server.ServerId, tools);
                DynamicTools.ReplaceServerTools(server.ServerId, tools);

                foreach (var descriptor in tools)
                {
                    var localName = CreateUniqueLocalToolName(
                        toolRegistry,
                        server.ServerId,
                        descriptor.Name);
                    toolRegistry.Register(new McpToolProxy(
                        localName,
                        server.ServerId,
                        descriptor.Name,
                        descriptor,
                        session));
                }

                _startupMessages.Add($"MCP {server.ServerId}: connected ({tools.Count} tools).");
            }
            catch (Exception ex)
            {
                ConnectionManager.UpdateState(server.ServerId, McpConnectionState.Failed);
                _startupMessages.Add($"MCP {server.ServerId}: failed to connect: {ex.Message}");
            }
        }
    }

    private static string CreateUniqueLocalToolName(
        ToolRegistry toolRegistry,
        string serverId,
        string remoteToolName)
    {
        var candidate = BuildBaseToolName(serverId, remoteToolName);
        if (toolRegistry.Get(candidate) == null)
            return candidate;

        var suffix = "_" + ShortHash($"{serverId}:{remoteToolName}");
        var maxPrefixLength = Math.Max(1, 64 - suffix.Length);
        candidate = candidate.Length > maxPrefixLength
            ? candidate[..maxPrefixLength]
            : candidate;

        return candidate + suffix;
    }

    private static string BuildBaseToolName(string serverId, string remoteToolName)
    {
        var sanitizedServer = SanitizeNamePart(serverId, "server");
        var sanitizedTool = SanitizeNamePart(remoteToolName, "tool");
        var candidate = $"mcp_{sanitizedServer}_{sanitizedTool}";
        if (candidate.Length <= 64)
            return candidate;

        var suffix = "_" + ShortHash($"{serverId}:{remoteToolName}");
        var maxPrefixLength = Math.Max(1, 64 - suffix.Length);
        return candidate[..maxPrefixLength] + suffix;
    }

    private static string SanitizeNamePart(string value, string fallback)
    {
        var buffer = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            buffer.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-'
                ? char.ToLowerInvariant(ch)
                : '_');
        }

        var sanitized = buffer.ToString().Trim('_');
        while (sanitized.Contains("__", StringComparison.Ordinal))
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes[..4]).ToLowerInvariant();
    }
}
