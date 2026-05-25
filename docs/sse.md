# MCP over SSE — operational notes

ArchMind exposes the Model Context Protocol (Streamable HTTP transport) at
`/mcp/{workspaceSlug}`. The GET form of that route is a long-lived Server-Sent
Events (SSE) stream and must survive idle periods of minutes at a time. This
document captures the contracts that operators and reverse-proxy authors must
honor so MCP clients see a stable channel.

## Endpoint contract

### Authentication

MCP clients do **not** use the admin-UI session cookie. Instead each request
carries a workspace-scoped bearer token:

```
Authorization: Bearer <workspace-api-key>
```

The same token is validated for both POST and GET (see
`backend/ArchMind.Api/Mcp/BearerAuthMiddleware.cs`).

### Connection sequence

1. **`POST /mcp/{workspaceSlug}`** with the JSON-RPC `initialize` method.
   - Response is `application/json` and includes a
     `Mcp-Session-Id: <guid>` header.
2. **`GET /mcp/{workspaceSlug}`** with both the bearer token and
   `Mcp-Session-Id: <guid>` from step 1. The server opens an SSE stream.
   - If the client omits `Mcp-Session-Id` the server creates a transient
     session for the lifetime of the stream and returns the new id in the
     response header.
3. **`POST /mcp/{workspaceSlug}`** for every subsequent JSON-RPC request,
   carrying the same `Mcp-Session-Id` header so the server can correlate.

### Response headers on the SSE stream

The GET handler (`McpEndpoints.HandleGetAsync`) emits:

| Header              | Value              | Why                                  |
|---------------------|--------------------|--------------------------------------|
| `Content-Type`      | `text/event-stream`| SSE wire format.                     |
| `Cache-Control`     | `no-cache`         | Defeat HTTP caches.                  |
| `Connection`        | `keep-alive`       | Hint to HTTP/1.1 intermediaries.     |
| `X-Accel-Buffering` | `no`               | Disable nginx / proxy buffering.     |
| `Mcp-Session-Id`    | `<guid>`           | Only emitted when the server allocates a transient session. |

### Frame format

- Data frames: `data: <utf-8 payload>\n\n` (one JSON-RPC envelope per frame).
- Heartbeat: SSE comment `: ping\n\n` written every **15 seconds** of idle
  time. Comments are ignored by EventSource clients but keep intermediaries
  from declaring the connection idle.
- The server flushes the response body after every frame.

## Server tuning (already applied)

Kestrel is configured in `backend/ArchMind.Api/Program.cs`:

```csharp
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    o.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
    // No per-request timeout for SSE — handled by app-level cancellation.
});
```

- `KeepAliveTimeout = 10min` is well above the 15s heartbeat cadence.
- There is intentionally **no `RequestHeadersTimeout` / per-request timeout**
  for SSE — disconnects are driven by the request `CancellationToken`.

Docker compose (`docker-compose.yml`):

- `stop_grace_period: 30s` lets in-flight SSE streams drain on SIGTERM
  before the container is killed.
- `healthcheck` probes `GET /health` every 15s.

## Reverse proxy / intermediary requirements

There is **no nginx or traefik in front of the backend today**. If one is
added later, it must satisfy all of the following or the MCP channel will
break in non-obvious ways (clients see truncation, infinite reconnects, or
duplicated frames):

### Required headers / behavior

- Forward `X-Accel-Buffering: no` from origin and honor it (nginx does this
  natively). If the proxy strips or overrides it, set the equivalent on the
  proxy itself:
  - **nginx**: `proxy_buffering off;` and `proxy_cache off;` on the MCP location.
  - **traefik**: no built-in buffering on HTTP/1.1, but ensure no
    `compress` middleware is applied to `text/event-stream`.
  - **Cloudflare**: SSE works on the Free plan but only on
    Enterprise-tier proxies with response buffering disabled. Document the
    tier requirement before fronting prod with Cloudflare.
- Preserve `Cache-Control: no-cache` end-to-end.

### Timeouts

- **Idle / read timeout: ≥ 10 minutes.** The 15s heartbeat keeps the socket
  warm, but proxies often kill streams on an absolute idle deadline rather
  than a TCP-quiet timer. Configure:
  - nginx: `proxy_read_timeout 600s;`
  - traefik: `forwardingTimeouts.responseHeaderTimeout = 0s` and
    `transport.respondingTimeouts.idleTimeout = 10m`.
  - HAProxy: `timeout server 10m` and `timeout tunnel 10m`.

### HTTP version

- HTTP/1.1 is the simplest path. SSE works fine.
- If using HTTP/2 termination, make sure stream-level idle timeouts are
  raised to match (`http2_idle_timeout` on nginx). HTTP/2 will multiplex but
  still applies per-stream deadlines.
- Disable any "smart" features that terminate long-lived requests:
  Cloudflare's `Always Online`, AWS ALB's `slow_start`, and similar.

### Compression

- Do **not** apply `gzip`/`br` to `text/event-stream`. Compression buffers
  data until enough bytes accumulate; clients will hang for minutes between
  frames.

## Client reconnect strategy

EventSource handles reconnects automatically but does not re-issue
`initialize`. ArchMind clients should:

1. On socket close or `error` event, sleep with **exponential backoff**:
   start at 1s, multiply by 2 on each consecutive failure, cap at 30s.
2. After each backoff sleep, **re-POST `initialize`** to obtain a fresh
   `Mcp-Session-Id` (the previous one may have been evicted from
   `InMemoryMcpSessionStore`).
3. Re-open the GET stream with the new session id.
4. Reset the backoff to 1s after a successful frame has been received.

A reference reconnect loop (pseudocode):

```ts
let delay = 1_000;
while (true) {
  try {
    const session = await initialize();        // POST /mcp/{slug}
    await openSseStream(session.id, onFrame);  // GET  /mcp/{slug}
    delay = 1_000;                             // success — reset backoff
  } catch (err) {
    await sleep(delay);
    delay = Math.min(delay * 2, 30_000);
  }
}
```

## Verifying locally

```bash
# 1. Open a session.
curl -i -X POST http://localhost:5000/mcp/my-workspace \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'

# 2. Subscribe to the stream (look for `: ping` every ~15s).
curl -N http://localhost:5000/mcp/my-workspace \
  -H "Authorization: Bearer $TOKEN" \
  -H "Mcp-Session-Id: <guid from step 1>"
```
