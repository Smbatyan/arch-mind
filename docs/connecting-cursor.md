# Connecting Cursor to ArchMind

This guide wires the [Cursor](https://cursor.com) IDE into your ArchMind
workspace via the Model Context Protocol so Cursor can call
`get_relevant_context`, list endpoints, find callers, and pull skills
straight from your repo graph.

Companion: `docs/sse.md` (transport details), `docs/connecting-claude-code.md`
(same flow for the Claude Code CLI).

---

## Prerequisites

- An ArchMind instance reachable from your machine. Local
  `docker compose up` is fine; remote requires HTTPS + the proxy notes in
  `docs/sse.md`.
- Your workspace already created and at least one repo scanned (so the
  graph isn't empty).
- Cursor installed. MCP support landed in Cursor 0.42; if your build
  is older, update from **Cursor → Settings → About → Check for
  updates**.

---

## Step 1 — Generate a workspace API key

The same flow as Claude Code:

1. Open the admin UI, e.g. `http://localhost:3000`.
2. Pick the workspace you want Cursor to see.
3. **Settings → API keys → Create key**. Name it something traceable,
   e.g. `cursor-laptop`.
4. Copy the token. The UI shows it **once**; if you lose it, generate a
   new one.

API keys are workspace-scoped and do **not** carry admin permissions.
They are safe to drop into IDE config.

---

## Step 2 — Note your MCP endpoint URL

```
<archmind-base-url>/mcp/<workspace-slug>
```

Examples:

- Local dev: `http://localhost:5000/mcp/my-workspace`
- Local dev via the dev proxy: `http://localhost:5050/mcp/my-workspace`
- Hosted: `https://archmind.example.com/mcp/my-workspace`

The workspace slug is shown in the URL of every admin-UI page
(`/workspaces/<slug>/...`).

---

## Step 3 — Register the MCP server with Cursor

Cursor reads `.cursor/mcp.json` from two places:

- `~/.cursor/mcp.json` — applies to every project (recommended for
  per-workspace ArchMind keys).
- `<project>/.cursor/mcp.json` — applies to this project only. Useful
  when different repos belong to different ArchMind workspaces.

Create the file if it does not exist:

```json
{
  "mcpServers": {
    "archmind": {
      "url": "http://localhost:5000/mcp/my-workspace",
      "headers": {
        "Authorization": "Bearer <paste-token-here>"
      }
    }
  }
}
```

Notes:

- The server name `archmind` is local-only. Use whatever you like; it
  shows up in Cursor's MCP panel and in agent tool-call labels.
- No `command` / `args` keys — those are for stdio-based servers.
  ArchMind speaks the Streamable HTTP transport so only `url` and
  `headers` are needed.

Restart Cursor (full quit + relaunch) so the config is re-read.

---

## Step 4 — Verify

1. In Cursor, open **Settings → Features → MCP**.
2. Find the `archmind` row. The status indicator should turn green
   within a few seconds.
3. Expanding the row should list the tools:
   `get_relevant_context`, `list_api_endpoints`, `find_callers`,
   `get_microservice`, `search_concepts`, `get_file_extraction`.

Then ask the Cursor chat (Cmd-L on macOS) a question only the repo
graph can answer, for example:

> Does the backend have a `/api/me` endpoint? If so, where is it defined?

If Cursor's MCP integration is healthy you'll see the agent call
`get_relevant_context` (the tool call appears in the chat transcript)
and respond with concrete file references from your ArchMind workspace.

---

## Step 5 — Daily use

Cursor decides per-turn whether to invoke MCP tools. Two habits help:

- Mention concrete things (`/api/users`, `OrderPlaced`, the service
  name) in your prompt. The smart endpoint matches on these tokens.
- Pin a Skill for big tasks. Skills authored in the admin UI are
  returned by `get_relevant_context` and steer Cursor more strongly
  than raw graph data.

---

## Troubleshooting

### MCP panel shows `archmind   failed` (red dot)

Test the handshake with curl:

```bash
curl -i -X POST http://localhost:5000/mcp/my-workspace \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}'
```

| HTTP status | Likely cause | Fix |
|---|---|---|
| 401 Unauthorized | Bad / revoked token, or wrong workspace slug | Regenerate the API key. Confirm slug in the admin URL. |
| 404 Not Found | Wrong path | The route is `/mcp/<slug>`, not `/mcp` or `/api/mcp`. |
| 502 / connection refused | ArchMind not running | `docker compose ps`; restart the `backend` service. |
| 200 but no `Mcp-Session-Id` header | Old backend, MCP not enabled | Pull latest; rebuild the backend image. |

### "Cursor MCP panel is empty"

Cursor only re-reads `~/.cursor/mcp.json` on full restart. Quit the
app (Cmd-Q on macOS) and relaunch — reload window is not enough.

### "MCP is connected but tools never get called"

Cursor's planner decides whether to use tools. Make sure:

- Cursor is set to **Agent** mode, not **Chat**, for the conversation
  in question. Plain Chat doesn't call tools.
- Your prompt mentions concrete identifiers (a path, an event name, a
  service name). Vague prompts let the agent fall back to plain RAG.

### "MCP works in curl but Cursor reports `Invalid response from server`"

Two common gotchas:

- **Trailing slash.** Some Cursor builds reject `…/mcp/<slug>/` (with
  trailing slash). Configure the URL without the trailing slash.
- **Self-signed TLS.** Cursor's MCP HTTP client refuses self-signed
  certs by default. Use HTTP for local dev, or put a real cert
  (e.g. mkcert + a trusted root) in front of the backend.

### "I see lots of `Mcp-Session-Id` mismatches in the logs"

That's harmless if the request still returns 200 — the server creates a
transient session and tells the client the new id via the
`Mcp-Session-Id` response header. The client picks it up on the next
request. See `docs/sse.md` §"Connection sequence".

---

## Revoking access

Open the admin UI, **Settings → API keys**, click **Revoke** on the
relevant row. The next MCP request from any Cursor instance using that
token returns 401 within seconds.
