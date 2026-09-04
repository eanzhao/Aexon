namespace Aexon.Core.Mcp;

/// <summary>
/// Represents a resource exposed by an MCP server.
/// </summary>
public sealed record McpResourceDescriptor(
    string Uri,
    string Name,
    string? Description,
    string? MimeType);

/// <summary>
/// Represents a content entry returned from an MCP resource read call.
/// </summary>
public sealed record McpResourceContent(
    string Uri,
    string? MimeType,
    string? Text,
    string? Blob);

/// <summary>
/// Represents the normalized result of an MCP resource read call.
/// </summary>
public sealed record McpReadResourceResult(
    IReadOnlyList<McpResourceContent> Contents);
