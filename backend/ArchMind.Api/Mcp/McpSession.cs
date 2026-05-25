using System.Threading.Channels;

namespace ArchMind.Api.Mcp;

/// <summary>
/// Represents a logical MCP session between a remote AI agent and the ArchMind server.
/// Each session is bound to a single workspace and carries an in-process channel used
/// to fan SSE writes from background work (e.g. <c>tools/call</c>) into the open stream.
/// </summary>
public sealed class McpSession
{
    public McpSession(Guid id, Guid workspaceId, DateTime createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;

        // Unbounded for the scaffold — bounded + backpressure will land alongside tools/call.
        Channel = System.Threading.Channels.Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public Guid Id { get; }
    public Guid WorkspaceId { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastActivityAt { get; set; }

    /// <summary>
    /// Pre-serialized SSE <c>data:</c> payloads pending delivery to the client.
    /// </summary>
    public Channel<string> Channel { get; }
}
