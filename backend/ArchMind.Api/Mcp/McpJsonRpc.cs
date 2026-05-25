using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchMind.Api.Mcp;

/// <summary>
/// JSON-RPC 2.0 message types used by the Model Context Protocol.
/// Spec: https://www.jsonrpc.org/specification and https://modelcontextprotocol.io.
/// </summary>
public sealed record McpRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

public sealed record McpResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Result,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpError? Error);

public sealed record McpError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Data);

/// <summary>
/// Standard JSON-RPC 2.0 error codes plus a few MCP-specific extensions.
/// </summary>
public static class McpErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}
