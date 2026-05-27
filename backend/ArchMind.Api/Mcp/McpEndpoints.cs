using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArchMind.Api.Mcp.Tools;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchMind.Api.Mcp;

/// <summary>
/// Endpoints implementing the Model Context Protocol Streamable HTTP transport
/// (https://modelcontextprotocol.io). Routes are mounted at <c>/mcp/{workspaceSlug}</c>
/// and are intentionally <see cref="AllowAnonymousAttribute"/>-friendly because MCP clients
/// authenticate with a workspace-scoped bearer token, not the admin-UI session cookie.
/// </summary>
public static class McpEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/mcp/{workspaceSlug}");

        group.MapPost("", HandlePostAsync).AllowAnonymous();
        group.MapGet("", HandleGetAsync).AllowAnonymous();

        return app;
    }

    // -----------------------------------------------------------------------
    // POST /mcp/{workspaceSlug}
    //
    // Client posts a JSON-RPC 2.0 request (or notification). For handshake
    // methods we currently respond with plain application/json — full SSE
    // streaming will arrive with tools/call in Wave 2.
    // -----------------------------------------------------------------------
    private static async Task HandlePostAsync(
        HttpContext httpContext,
        string workspaceSlug,
        ArchMindDbContext db,
        IMcpSessionStore sessionStore,
        McpHandshakeHandler handshakeHandler,
        McpToolsHandler toolsHandler,
        McpResourcesHandler resourcesHandler,
        McpPromptsHandler promptsHandler,
        IGraphReader graphReader,
        IFileExtractionRepository fileExtractions,
        GetRelevantContextHandler relevantContext,
        ITelemetryRecorder telemetryRecorder,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("ArchMind.Api.Mcp");
        var startTicks = Stopwatch.GetTimestamp();
        var requestSize = httpContext.Request.ContentLength is long len && len <= int.MaxValue
            ? (int?)len
            : null;

        var auth = await McpBearerAuth.AuthenticateAsync(httpContext, workspaceSlug, db, ct);
        if (!auth.Succeeded)
        {
            await WriteJsonAsync(httpContext, auth.StatusCode, new
            {
                jsonrpc = "2.0",
                error = new { code = McpErrorCodes.InvalidRequest, message = auth.Error },
            });
            RecordTelemetry(
                telemetryRecorder, logger, Guid.Empty, null,
                method: "auth", statusCode: auth.StatusCode,
                startTicks: startTicks, requestSize: requestSize, responseSize: null,
                errorMessage: auth.Error);
            return;
        }

        McpRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<McpRequest>(
                httpContext.Request.Body, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Malformed JSON-RPC payload from workspace {WorkspaceId}.", auth.WorkspaceId);
            await WriteParseErrorAsync(httpContext);
            RecordTelemetry(
                telemetryRecorder, logger, auth.WorkspaceId, auth.ApiKeyId,
                method: "parse_error", statusCode: StatusCodes.Status400BadRequest,
                startTicks: startTicks, requestSize: requestSize, responseSize: null,
                errorMessage: ex.Message);
            return;
        }

        if (request is null || string.IsNullOrEmpty(request.Method) || request.JsonRpc != "2.0")
        {
            await WriteParseErrorAsync(httpContext);
            RecordTelemetry(
                telemetryRecorder, logger, auth.WorkspaceId, auth.ApiKeyId,
                method: "parse_error", statusCode: StatusCodes.Status400BadRequest,
                startTicks: startTicks, requestSize: requestSize, responseSize: null,
                errorMessage: "invalid json-rpc envelope");
            return;
        }

        // Resolve session id from header if present; otherwise a session is created on initialize.
        Guid? sessionId = TryReadSessionId(httpContext);
        if (request.Method == "initialize")
        {
            var session = sessionStore.Create(auth.WorkspaceId, auth.ApiKeyId);
            sessionId = session.Id;
            httpContext.Response.Headers["Mcp-Session-Id"] = session.Id.ToString();
        }
        else if (sessionId is Guid sid)
        {
            sessionStore.Touch(sid);
        }

        logger.LogInformation(
            "MCP method received. workspace={WorkspaceId} session={SessionId} method={Method}",
            auth.WorkspaceId,
            sessionId,
            request.Method);

        // Notification with no id → 202 Accepted and we're done.
        var isNotification = request.Id is null || request.Id.Value.ValueKind == JsonValueKind.Null
            || McpHandshakeHandler.IsKnownNotification(request.Method);
        if (isNotification)
        {
            // Still let the handler observe it (e.g. initialized).
            handshakeHandler.TryHandle(request, auth.WorkspaceId, sessionId);
            httpContext.Response.StatusCode = StatusCodes.Status202Accepted;
            httpContext.Response.Headers["Connection"] = "close";
            RecordTelemetry(
                telemetryRecorder, logger, auth.WorkspaceId, auth.ApiKeyId,
                method: request.Method, statusCode: StatusCodes.Status202Accepted,
                startTicks: startTicks, requestSize: requestSize, responseSize: 0,
                errorMessage: null);
            return;
        }

        // Dispatch order:
        //  1) handshake methods (initialize, ping, prompts/list, …)
        //  2) tools/list, tools/call
        //  3) resources/list, resources/read
        //  4) MethodNotFound
        McpResponse? response = handshakeHandler.TryHandle(request, auth.WorkspaceId, sessionId);
        var resolvedMethod = request.Method;

        if (response is null)
        {
            switch (request.Method)
            {
                case "tools/list":
                    response = toolsHandler.HandleList(request);
                    break;
                case "tools/call":
                    var (toolResp, toolMethod) = await toolsHandler.HandleCallAsync(
                        request, auth.WorkspaceId, graphReader, fileExtractions, relevantContext, ct);
                    response = toolResp;
                    resolvedMethod = toolMethod;
                    break;
                case "resources/list":
                    response = resourcesHandler.HandleList(request, workspaceSlug);
                    break;
                case "resources/read":
                    response = await resourcesHandler.HandleReadAsync(
                        request, auth.WorkspaceId, workspaceSlug, graphReader, db, ct);
                    break;
                case "prompts/list":
                    response = await promptsHandler.HandleListAsync(
                        request, auth.WorkspaceId, db, ct);
                    break;
                case "prompts/get":
                    response = await promptsHandler.HandleGetAsync(
                        request, auth.WorkspaceId, db, ct);
                    break;
                default:
                    response = new McpResponse(
                        JsonRpc: "2.0",
                        Id: request.Id,
                        Result: null,
                        Error: new McpError(
                            Code: McpErrorCodes.MethodNotFound,
                            Message: $"method not found: {request.Method}",
                            Data: null));
                    break;
            }
        }

        var (responseBytes, responseSize) = SerializeResponse(response);
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        httpContext.Response.ContentLength = responseBytes.Length;
        // Force a fresh TCP connection for each request. Claude Code's HTTP MCP
        // transport pipelines multiple requests on the same connection, but the
        // current handler does not interoperate with that — the second request
        // on a keep-alive connection hangs. Until the underlying cause is fixed,
        // signaling close keeps every client compatible.
        httpContext.Response.Headers["Connection"] = "close";
        await httpContext.Response.Body.WriteAsync(responseBytes, ct);

        var status = response.Error is null ? StatusCodes.Status200OK : MapJsonRpcErrorToHttp(response.Error.Code);
        RecordTelemetry(
            telemetryRecorder, logger, auth.WorkspaceId, auth.ApiKeyId,
            method: resolvedMethod, statusCode: status,
            startTicks: startTicks, requestSize: requestSize, responseSize: responseSize,
            errorMessage: response.Error?.Message);
    }

    private static (byte[] Bytes, int Size) SerializeResponse(McpResponse response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        return (bytes, bytes.Length);
    }

    private static int MapJsonRpcErrorToHttp(int code) => code switch
    {
        McpErrorCodes.ParseError => StatusCodes.Status400BadRequest,
        McpErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        McpErrorCodes.MethodNotFound => StatusCodes.Status404NotFound,
        McpErrorCodes.InvalidParams => StatusCodes.Status400BadRequest,
        McpErrorCodes.InternalError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status200OK,
    };

    /// <summary>
    /// Best-effort telemetry write. Discards exceptions on the background task
    /// so the hot path never observes a telemetry failure.
    /// </summary>
    private static void RecordTelemetry(
        ITelemetryRecorder recorder,
        ILogger logger,
        Guid workspaceId,
        Guid? apiKeyId,
        string method,
        int statusCode,
        long startTicks,
        int? requestSize,
        int? responseSize,
        string? errorMessage)
    {
        var latencyMs = (int)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        var trimmed = errorMessage is { Length: > 4000 } e ? e[..4000] : errorMessage;

        var entry = new McpTelemetryEntry
        {
            WorkspaceId = workspaceId,
            ApiKeyId = apiKeyId,
            Method = method.Length > 200 ? method[..200] : method,
            StatusCode = statusCode,
            LatencyMs = latencyMs,
            RequestSizeBytes = requestSize,
            ResponseSizeBytes = responseSize,
            ErrorMessage = trimmed,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Telemetry must never delay the response path. The recorder swallows
        // its own exceptions; the ContinueWith here is just belt-and-braces
        // against unexpected synchronous throws.
        _ = recorder.RecordMcpAsync(entry, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.Exception is not null)
                {
                    logger.LogWarning(t.Exception,
                        "MCP telemetry write threw unexpectedly. workspace={WorkspaceId}",
                        workspaceId);
                }
            }, TaskScheduler.Default);
    }

    // -----------------------------------------------------------------------
    // GET /mcp/{workspaceSlug}
    //
    // Optional long-lived SSE channel — clients use this to receive server-
    // initiated events (e.g. notifications). For the scaffold we open the
    // stream, emit a comment heartbeat, and keep it open until the client
    // disconnects.
    // -----------------------------------------------------------------------
    private static async Task HandleGetAsync(
        HttpContext httpContext,
        string workspaceSlug,
        ArchMindDbContext db,
        IMcpSessionStore sessionStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("ArchMind.Api.Mcp");

        var auth = await McpBearerAuth.AuthenticateAsync(httpContext, workspaceSlug, db, ct);
        if (!auth.Succeeded)
        {
            await WriteJsonAsync(httpContext, auth.StatusCode, new { error = auth.Error });
            return;
        }

        var sessionId = TryReadSessionId(httpContext);
        McpSession? session = sessionId is Guid sid ? sessionStore.Get(sid) : null;

        // If the client never opened a session via initialize, create a transient one so the
        // stream can still emit heartbeats. The session is removed on disconnect.
        var transient = false;
        if (session is null)
        {
            session = sessionStore.Create(auth.WorkspaceId);
            transient = true;
            httpContext.Response.Headers["Mcp-Session-Id"] = session.Id.ToString();
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.Headers["Content-Type"] = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"] = "no-cache";
        httpContext.Response.Headers["Connection"] = "keep-alive";
        // Defeat proxy buffering (nginx, traefik, Cloudflare) so SSE frames
        // reach the client immediately. Harmless when no proxy is present.
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        logger.LogInformation(
            "MCP SSE stream opened. workspace={WorkspaceId} session={SessionId}",
            auth.WorkspaceId,
            session.Id);

        var writer = httpContext.Response.BodyWriter;
        try
        {
            // Initial comment to flush headers immediately.
            await WriteSseCommentAsync(httpContext, "stream ready", ct);

            // Drain queued events; fall back to a periodic heartbeat so proxies don't drop us.
            var heartbeat = TimeSpan.FromSeconds(15);
            while (!ct.IsCancellationRequested)
            {
                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hbCts.CancelAfter(heartbeat);
                try
                {
                    var payload = await session.Channel.Reader.ReadAsync(hbCts.Token);
                    await WriteSseDataAsync(httpContext, payload, ct);
                    sessionStore.Touch(session.Id);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteSseCommentAsync(httpContext, "ping", ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected.
        }
        finally
        {
            if (transient)
            {
                sessionStore.Remove(session.Id);
            }
            logger.LogInformation(
                "MCP SSE stream closed. workspace={WorkspaceId} session={SessionId}",
                auth.WorkspaceId,
                session.Id);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static Guid? TryReadSessionId(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("Mcp-Session-Id", out var values)
            && Guid.TryParse(values.ToString(), out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static Task WriteJsonAsync(HttpContext httpContext, int statusCode, object payload)
    {
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(httpContext.Response.Body, payload, JsonOptions);
    }

    private static async Task WriteParseErrorAsync(HttpContext httpContext)
    {
        var response = new McpResponse(
            JsonRpc: "2.0",
            Id: null,
            Result: null,
            Error: new McpError(McpErrorCodes.ParseError, "parse error", null));
        await WriteJsonAsync(httpContext, StatusCodes.Status400BadRequest, response);
    }

    private static async Task WriteSseDataAsync(HttpContext httpContext, string data, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await httpContext.Response.Body.WriteAsync(bytes, ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }

    private static async Task WriteSseCommentAsync(HttpContext httpContext, string comment, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($": {comment}\n\n");
        await httpContext.Response.Body.WriteAsync(bytes, ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }
}
