# Connecting Claude Code to ArchMind

This guide wires the [Claude Code](https://docs.anthropic.com/en/docs/claude-code/overview)
CLI into your ArchMind workspace so the agent can call `get_relevant_context`,
list endpoints, find callers, and pull skills directly from your repo graph.

Companion: `docs/sse.md` (transport details), `docs/connecting-cursor.md`
(same flow for Cursor).

---

## Prerequisites

- An ArchMind instance reachable from your machine. Local
  `docker compose up` is fine; remote requires HTTPS + the proxy notes in
  `docs/sse.md`.
- Your workspace already created and at least one repo scanned (so the
  graph isn't empty).
- Claude Code installed and authenticated:
  - `npm install -g @anthropic-ai/claude-code`
  - `claude --version` prints a version number.

---

## Step 1 — Generate a workspace API key

API keys are workspace-scoped. They authenticate MCP traffic and do
**not** carry admin permissions.

1. Open the admin UI, e.g. `http://localhost:3000`.
2. Pick the workspace you want Claude Code to see.
3. **Settings → API keys → Create key**. Give it a name like
   `claude-code-laptop` so you can revoke it later without guessing.
4. Copy the token. The UI shows it **once**; if you lose it, generate a
   new one. The token format is opaque — don't try to decode it.

The same screen lists active keys with last-used timestamps, useful for
spotting stale machines.

---

## Step 2 — Note your MCP endpoint URL

The MCP endpoint is:

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

## Step 3 — Register the MCP server with Claude Code

Pick **one** of the two methods below.

### Method A — `claude mcp add` (recommended)

This writes the config for you and avoids JSON typos.

```bash
claude mcp add --transport http archmind \
  http://localhost:5000/mcp/my-workspace \
  --header "Authorization: Bearer <paste-token-here>"
```

- `--transport http` selects the Streamable HTTP transport used by
  ArchMind. Don't use `sse` or `stdio` — they're for different servers.
- The server name `archmind` is local-only. Use whatever you like; it's
  what appears in `/mcp` inside Claude Code.

To wire the same server for **every** project on the machine, add
`--scope user` (writes to `~/.claude.json`). Default scope is project-local
(`./.claude.json`).

### Method B — Edit `.claude.json` by hand

Place this in `~/.claude.json` (user-wide) or `./.claude.json` (this
project only):

```json
{
  "mcpServers": {
    "archmind": {
      "type": "http",
      "url": "http://localhost:5000/mcp/my-workspace",
      "headers": {
        "Authorization": "Bearer <paste-token-here>"
      }
    }
  }
}
```

Restart Claude Code for the change to be picked up.

---

## Step 4 — Verify

Start (or restart) Claude Code in any directory:

```bash
claude
```

Inside the prompt, run the MCP debugger:

```
/mcp
```

You should see:

```
archmind   connected
  resources  N
  tools      get_relevant_context, list_api_endpoints,
             find_callers, get_microservice,
             search_concepts, get_file_extraction
```

Then ask a question that the codebase can answer, for example:

> Does the backend have a `/api/me` endpoint? If so, where is it defined?

Claude Code will call `get_relevant_context` against your workspace and
return service + file references straight from the ArchMind graph and
fresh extraction layer.

If `/mcp` shows `archmind   failed`, jump to **Troubleshooting** below.

---

## Step 5 — Daily use

You don't need to do anything special — Claude Code decides per-turn
whether to call MCP tools. Two habits help:

- Mention concrete things (`/api/users`, `OrderPlaced`, the service
  name) in your prompt. The smart endpoint matches on these.
- After a big merge, force-refresh the graph from the admin UI
  (**Repos → repo → Re-scan**) so MCP returns post-merge data.

---

## Troubleshooting

### `/mcp` shows `archmind   failed`

Run the handshake manually with curl:

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
| 200 but no `Mcp-Session-Id` header | Old backend, MCP not enabled | Pull latest; rebuild backend image. |

### "I added the key but `/mcp` shows zero tools."

The handshake completed but the workspace exists with no data yet.
Connect a repo and wait for the initial scan to finish before asking
questions.

### "MCP works in curl but Claude Code says `archmind   failed`."

Two common gotchas:

- **Wrong transport.** ArchMind speaks Streamable HTTP, not `stdio`.
  In `claude mcp add` use `--transport http`. In `.claude.json` use
  `"type": "http"`.
- **Stale config.** Edits to `.claude.json` only take effect on Claude
  Code restart. Quit the CLI entirely and start it again.

### "The agent keeps re-reading my whole repo even though MCP is connected."

That means MCP responded but with too little signal for the agent to
anchor on. Two things to try:

1. Re-scan the repo in ArchMind to refresh the graph; some sections
   (endpoints, events) only populate after extraction completes.
2. Author or import a Skill that frames the task ("How we add an
   endpoint"). Skills are returned by `get_relevant_context` and steer
   the agent more strongly than raw graph data.

### "Connection drops after a few minutes of idle."

The MCP SSE channel uses 15s heartbeats and a 10-min Kestrel keep-alive.
If you're behind a corporate proxy, see `docs/sse.md` §"Reverse proxy /
load balancer requirements" — the proxy must allow long-lived
connections and disable response buffering.

---

## Revoking access

Open the admin UI, **Settings → API keys**, click **Revoke** on the
relevant row. The next MCP request from any client using that token
returns 401 within seconds.
