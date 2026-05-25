using System.Text.Json;
using System.Text.Json.Serialization;
using ArchMind.Api.Mcp.Tools;
using ArchMind.Core.Abstractions;

namespace ArchMind.Api.Mcp;

/// <summary>
/// MCP <c>tools/list</c> and <c>tools/call</c> handler (BE-030).
///
/// Each tool input is described by an inline JSON Schema. The dispatcher takes
/// the resolved workspace id from <see cref="HttpContext.Items"/> and refuses
/// any client attempt to override it via tool arguments.
/// </summary>
public sealed class McpToolsHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<McpToolsHandler> _logger;

    public McpToolsHandler(ILogger<McpToolsHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>The fully qualified MCP method that the telemetry recorder will store.</summary>
    public const string CallMethod = "tools/call";

    // -----------------------------------------------------------------------
    // tools/list
    // -----------------------------------------------------------------------
    public McpResponse HandleList(McpRequest request)
    {
        var tools = new object[]
        {
            new
            {
                name = "search_concepts",
                description = "Case-insensitive text search across workspace graph nodes (services, conventions, capabilities, etc.). Matches `name` and `description` properties.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", description = "Text to search for." },
                        ["limit"] = new { type = "integer", minimum = 1, maximum = 100, @default = 10 },
                    },
                    required = new[] { "query" },
                },
            },
            new
            {
                name = "get_microservice",
                description = "Returns a microservice (Service node) and its 1-hop neighborhood: endpoints, dependencies, published and consumed events.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["name"] = new { type = "string", description = "Service name (exact match)." },
                    },
                    required = new[] { "name" },
                },
            },
            new
            {
                name = "list_api_endpoints",
                description = "Lists HTTP endpoint nodes in the workspace, optionally filtered by owning microservice name and / or HTTP method.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["microservice"] = new { type = "string", description = "Owning service name." },
                        ["method"] = new { type = "string", description = "HTTP method (GET, POST, ...)." },
                    },
                    required = Array.Empty<string>(),
                },
            },
            new
            {
                name = "find_callers",
                description = "Returns inbound callers of an HTTP endpoint, traversing CALLS edges. Identify the endpoint by id, or by (method, path).",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["endpoint_id"] = new { type = "string", description = "Endpoint node id (uuid)." },
                        ["method"] = new { type = "string" },
                        ["path"] = new { type = "string" },
                    },
                    required = Array.Empty<string>(),
                },
            },
            new
            {
                name = "get_file_extraction",
                description = "Returns the latest LLM extraction payload for a single source file path in this workspace.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["file_path"] = new { type = "string", description = "Repo-relative path." },
                    },
                    required = new[] { "file_path" },
                },
            },
            new
            {
                name = GetRelevantContextHandler.ToolName,
                description = "Returns matched user skills, relevant graph nodes, and file extraction summaries for an agent task, fitted to a token budget.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["task"] = new { type = "string", description = "Agent task description." },
                        ["repo"] = new { type = "string", description = "Optional repo identifier." },
                        ["file_paths"] = new { type = "array", items = new { type = "string" } },
                        ["max_tokens"] = new { type = "integer", minimum = 256, maximum = 32000, @default = 4000 },
                    },
                    required = new[] { "task" },
                },
            },
        };

        return BuildResponse(request.Id, new { tools });
    }

    // -----------------------------------------------------------------------
    // tools/call
    // -----------------------------------------------------------------------
    public async Task<(McpResponse Response, string ResolvedMethod)> HandleCallAsync(
        McpRequest request,
        Guid workspaceId,
        IGraphReader graphReader,
        IFileExtractionRepository fileExtractions,
        GetRelevantContextHandler relevantContext,
        CancellationToken ct)
    {
        var name = TryGetStringParam(request.Params, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return (Error(request.Id, McpErrorCodes.InvalidParams, "missing 'name' parameter"),
                $"{CallMethod}:<unknown>");
        }

        JsonElement args = default;
        if (request.Params is JsonElement p && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("arguments", out var argEl))
        {
            args = argEl;
        }

        var resolvedMethod = $"{CallMethod}:{name}";

        try
        {
            object? result = name switch
            {
                "search_concepts" => await CallSearchConceptsAsync(args, workspaceId, graphReader, ct),
                "get_microservice" => await CallGetMicroserviceAsync(args, workspaceId, graphReader, ct),
                "list_api_endpoints" => await CallListEndpointsAsync(args, workspaceId, graphReader, ct),
                "find_callers" => await CallFindCallersAsync(args, workspaceId, graphReader, ct),
                "get_file_extraction" => await CallGetFileExtractionAsync(args, workspaceId, fileExtractions, ct),
                "get_relevant_context" => await relevantContext.ExecuteAsync(workspaceId, args, ct),
                _ => null,
            };

            if (result is null && name is not "search_concepts" and not "get_microservice"
                and not "list_api_endpoints" and not "find_callers" and not "get_file_extraction"
                and not "get_relevant_context")
            {
                return (Error(request.Id, McpErrorCodes.InvalidParams, $"unknown tool: {name}"), resolvedMethod);
            }

            var text = JsonSerializer.Serialize(result ?? new { }, JsonOptions);
            var success = new
            {
                content = new[]
                {
                    new { type = "text", text },
                },
                isError = false,
            };
            return (BuildResponse(request.Id, success), resolvedMethod);
        }
        catch (ArgumentException ex)
        {
            // Validation errors → bad params surfaced inside the result envelope.
            var errPayload = new
            {
                content = new[]
                {
                    new { type = "text", text = ex.Message },
                },
                isError = true,
            };
            return (BuildResponse(request.Id, errPayload), resolvedMethod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tool execution failed. workspace={WorkspaceId} tool={Tool}", workspaceId, name);
            var errPayload = new
            {
                content = new[]
                {
                    new { type = "text", text = $"tool '{name}' failed: {ex.GetType().Name}" },
                },
                isError = true,
            };
            return (BuildResponse(request.Id, errPayload), resolvedMethod);
        }
    }

    // -----------------------------------------------------------------------
    // Tool implementations
    // -----------------------------------------------------------------------
    private static async Task<object> CallSearchConceptsAsync(
        JsonElement args, Guid workspaceId, IGraphReader graphReader, CancellationToken ct)
    {
        var query = TryGetString(args, "query")
            ?? throw new ArgumentException("'query' is required.");
        var limit = TryGetInt(args, "limit") ?? 10;

        // Single phrase as one token — the underlying reader handles substring
        // CONTAINS, so a multi-word query still works for "ContainsAll" callers
        // because we pass it as one token.
        var hits = await graphReader.SearchNodesByTextAsync(
            workspaceId, new[] { query }, limit, ct);

        return new
        {
            query,
            count = hits.Count,
            hits = hits.Select(h => new
            {
                id = h.Id,
                label = h.Label,
                name = h.Name,
                description = h.Description,
            }),
        };
    }

    private static async Task<object> CallGetMicroserviceAsync(
        JsonElement args, Guid workspaceId, IGraphReader graphReader, CancellationToken ct)
    {
        var name = TryGetString(args, "name")
            ?? throw new ArgumentException("'name' is required.");

        var neighborhood = await graphReader.GetServiceNeighborhoodAsync(workspaceId, name, ct);
        if (neighborhood is null)
        {
            return new { found = false, name };
        }

        return new
        {
            found = true,
            service = neighborhood.Service,
            endpoints = neighborhood.Endpoints,
            dependencies = neighborhood.Dependencies,
            publishes = neighborhood.Publishes,
            consumes = neighborhood.Consumes,
        };
    }

    private static async Task<object> CallListEndpointsAsync(
        JsonElement args, Guid workspaceId, IGraphReader graphReader, CancellationToken ct)
    {
        var microservice = TryGetString(args, "microservice");
        var method = TryGetString(args, "method");

        var endpoints = await graphReader.ListEndpointsAsync(workspaceId, microservice, method, ct);
        return new
        {
            microservice,
            method,
            count = endpoints.Count,
            endpoints,
        };
    }

    private static async Task<object> CallFindCallersAsync(
        JsonElement args, Guid workspaceId, IGraphReader graphReader, CancellationToken ct)
    {
        var endpointIdRaw = TryGetString(args, "endpoint_id");
        IReadOnlyList<Core.Models.Graph.EndpointCaller> callers;

        if (!string.IsNullOrWhiteSpace(endpointIdRaw))
        {
            if (!Guid.TryParse(endpointIdRaw, out var endpointId))
            {
                throw new ArgumentException("'endpoint_id' must be a valid uuid.");
            }
            callers = await graphReader.FindEndpointCallersByIdAsync(workspaceId, endpointId, ct);
            return new { endpointId = endpointId.ToString(), count = callers.Count, callers };
        }

        var method = TryGetString(args, "method");
        var path = TryGetString(args, "path");
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "either 'endpoint_id' or both 'method' and 'path' must be provided.");
        }
        callers = await graphReader.FindEndpointCallersByRouteAsync(workspaceId, method, path, ct);
        return new { method, path, count = callers.Count, callers };
    }

    private static async Task<object> CallGetFileExtractionAsync(
        JsonElement args, Guid workspaceId, IFileExtractionRepository repo, CancellationToken ct)
    {
        var filePath = TryGetString(args, "file_path")
            ?? throw new ArgumentException("'file_path' is required.");

        var rows = await repo.GetLatestForFilesAsync(workspaceId, new[] { filePath }, ct);
        var row = rows.FirstOrDefault();
        if (row is null)
        {
            return new { found = false, filePath };
        }

        return new
        {
            found = true,
            filePath = row.FilePath,
            contentHash = row.ContentHash,
            extraction = row.Record,
        };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static string? TryGetStringParam(JsonElement? root, string name)
    {
        if (root is null || root.Value.ValueKind != JsonValueKind.Object) return null;
        return root.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int? TryGetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    private static McpResponse BuildResponse(JsonElement? id, object result) =>
        new(JsonRpc: "2.0", Id: id, Result: result, Error: null);

    private static McpResponse Error(JsonElement? id, int code, string message) =>
        new(JsonRpc: "2.0", Id: id, Result: null, Error: new McpError(code, message, null));
}
