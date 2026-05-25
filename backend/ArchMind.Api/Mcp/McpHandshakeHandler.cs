using System.Text.Json;

namespace ArchMind.Api.Mcp;

/// <summary>
/// Handles the small set of synchronous, side-effect-free MCP methods that make up the
/// initial handshake plus the empty-capability listings. Tool / resource execution lives
/// in later waves and will stream over SSE instead of returning inline.
/// </summary>
public sealed class McpHandshakeHandler
{
    public const string ProtocolVersion = "2025-03-26";
    public const string ServerName = "archmind";
    public const string ServerVersion = "0.1.0";

    private readonly ILogger<McpHandshakeHandler> _logger;

    public McpHandshakeHandler(ILogger<McpHandshakeHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Try to dispatch a request that the handshake handler knows about.
    /// Returns <c>null</c> when the method is not a handshake-scope method so the
    /// endpoint can either route it elsewhere or return <c>MethodNotFound</c>.
    /// </summary>
    public McpResponse? TryHandle(McpRequest request, Guid workspaceId, Guid? sessionId)
    {
        switch (request.Method)
        {
            case "initialize":
                return BuildResponse(request.Id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new
                    {
                        resources = new { },
                        tools = new { },
                    },
                    serverInfo = new
                    {
                        name = ServerName,
                        version = ServerVersion,
                    },
                });

            case "initialized":
            case "notifications/initialized":
                // Pure notification — nothing to return. Caller handles 202 Accepted.
                return null;

            case "ping":
                return BuildResponse(request.Id, new { });

            case "prompts/list":
                return BuildResponse(request.Id, new { prompts = Array.Empty<object>() });

            // tools/list, tools/call, resources/list and resources/read are
            // dispatched to dedicated handlers (BE-029 / BE-030) by the
            // endpoint; they live outside the handshake-scope.
            default:
                return null;
        }
    }

    /// <summary>
    /// Returns true when the method is a JSON-RPC notification with no expected response,
    /// regardless of whether the request actually omitted <c>id</c>.
    /// </summary>
    public static bool IsKnownNotification(string method) =>
        method is "initialized"
            or "notifications/initialized"
            or "notifications/cancelled"
            or "notifications/progress";

    private static McpResponse BuildResponse(JsonElement? id, object result) =>
        new(JsonRpc: "2.0", Id: id, Result: result, Error: null);
}
