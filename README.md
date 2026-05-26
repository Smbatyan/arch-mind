# ArchMind

ArchMind builds a queryable knowledge graph of your microservices codebases and serves it to AI agents over the Model Context Protocol (MCP). It ingests source repositories, extracts architecture and code structure into a Postgres + Apache AGE graph, and exposes per-workspace MCP endpoints so agents can answer questions grounded in your real system.

## Prerequisites

- Docker 24+
- Docker Compose v2
- An Anthropic API key (Claude Haiku + Sonnet are used for ingestion and reasoning)

> .NET 10 SDK and Node 22+ are only required if you plan to run the backend or admin UI outside of containers (see [Local dev](#local-dev-no-docker)).

## Quick start (Docker)

```bash
# 1. Clone the repo
git clone <repo-url> archmind
cd archmind

# 2. Configure environment
cp .env.example .env
# Edit .env and set Anthropic__ApiKey, POSTGRES_PASSWORD, and any other secrets.
# See docs/secrets.md for the full list and rotation guidance.

# 3. Start everything
docker compose up -d
```

Once the stack is up:

- Admin UI: <http://localhost:3000>
- Hangfire dashboard: <http://localhost:5000/hangfire>
- MCP endpoint pattern: `http://localhost:5000/mcp/{workspace_slug}`

Check service health with `docker compose ps` and tail logs with `docker compose logs -f`.

### First login

ArchMind seeds a single admin user from `AdminSeed__Email` /
`AdminSeed__Password` in `.env` (defaults: `admin@archmind.local` /
`change-me-on-first-login`). The seed only runs against an empty users
table. **Change the password immediately** from the admin UI account
settings after logging in for the first time.

### Create a workspace and connect a repo

1. From the admin UI, click **New workspace**, give it a name (the slug is
   derived from the name and used in the MCP endpoint path).
2. In the workspace, open **Repositories → Add repository** and paste a
   Git URL. For private GitHub repos make sure the GitHub App is installed
   on the org and the credentials in `.env` are set.
3. The first scan kicks off immediately; subsequent scans run on the
   `Polling__IntervalMinutes` cadence.

### Connecting MCP clients

ArchMind exposes one MCP endpoint per workspace, gated by a workspace API
key.

1. In the admin UI, go to `/workspaces/{slug}/settings/api-keys` and
   generate a key. The full key is shown **once** — copy it now.
2. Configure your MCP client to point at
   `http://your-host:5000/mcp/{slug}` with header
   `Authorization: Bearer <key>`.

Example for **Claude Code** (`~/.claude/mcp-servers.json` or equivalent):

```json
{
  "mcpServers": {
    "archmind-acme": {
      "transport": {
        "type": "sse",
        "url": "http://your-host:5000/mcp/acme",
        "headers": { "Authorization": "Bearer ak_live_..." }
      }
    }
  }
}
```

Example for **Cursor** (`.cursor/mcp.json` in your project):

```json
{
  "mcpServers": {
    "archmind-acme": {
      "url": "http://your-host:5000/mcp/acme",
      "headers": { "Authorization": "Bearer ak_live_..." }
    }
  }
}
```

## Production deployment

- **Run behind an HTTPS terminator** (nginx, traefik, or a cloud load
  balancer). The backend itself speaks plain HTTP inside the container
  network; TLS is the terminator's job.
- **SSE buffering must be disabled at the proxy.** See `docs/sse.md` for
  the required nginx / traefik configuration (`proxy_buffering off`,
  generous read timeouts). MCP streams will appear to hang otherwise.
- Set `Auth__RequireHttps=true` so auth cookies carry the `Secure` flag.
- Set `Cors__AllowedOrigins__0=https://your-admin-domain` (one indexed
  entry per allowed origin).
- **Do not expose Postgres to the host.** Remove the `5432:5432` port
  mapping from `docker-compose.yml` for production; the backend reaches
  Postgres over the internal `archmind` network.
- **Inject secrets rather than mounting `.env`.** Prefer Docker Secrets
  or an external KV store (Vault, AWS Secrets Manager, Azure Key Vault).
  See `docs/secrets.md` for full guidance and the threat model.
- **Back up the `postgres_data` volume regularly.** AGE graph state and
  workspace metadata both live there. A standard `pg_dump` works; for
  AGE graphs include the `ag_catalog` schema. Snapshot the volume
  periodically and store off-host.

## Operations

### Health endpoints

| Endpoint               | Purpose |
| ---------------------- | ------- |
| `GET /health`          | Liveness — process is up. |
| `GET /health/db`       | Readiness — Postgres + AGE reachable. |
| `GET /smoke`           | Full smoke test (per BE-046): DB, AGE, Anthropic, Hangfire. |

```bash
curl http://localhost:5000/health
curl http://localhost:5000/health/db
curl http://localhost:5000/smoke
```

### Hangfire dashboard

`http://localhost:5000/hangfire` — requires an authenticated admin
session (the same cookie used by the admin UI).

### Logs

- Stdout JSON for every container: `docker compose logs -f backend`.
- Backend file logs at `/var/log/archmind/*.log` inside the container
  (bind-mounted to `./logs`). Daily rotation, 7-day retention.

## Updating

```bash
git pull
docker compose pull
docker compose up -d --build
```

- In **Development** / non-Production environments EF Core migrations
  apply automatically at backend startup.
- In **Production** (`ASPNETCORE_ENVIRONMENT=Production`) migrations do
  not auto-apply. Run them manually against the prod connection string
  before bringing the new backend image up:

  ```bash
  cd backend/ArchMind.Infrastructure
  dotnet ef database update \
    --startup-project ../ArchMind.Api \
    --connection "$ConnectionStrings__Default"
  ```

## Configuration

### Polling cadence

The backend periodically polls each tracked repo for changes via a Hangfire recurring job. Cadence is controlled by two settings:

| Key | Default | Description |
| --- | --- | --- |
| `Polling:CronExpression` | `*/30 * * * *` | Cron schedule (5-field: minute hour day-of-month month day-of-week) |
| `Polling:Enabled` | `true` | Master switch for polling jobs |

Set via environment variables (double underscore separator):

```bash
Polling__CronExpression="*/15 * * * *"   # every 15 minutes
Polling__CronExpression="0 * * * *"      # every hour
Polling__Enabled=false                   # disable polling entirely
```

Or in `appsettings.json`:

```json
"Polling": { "CronExpression": "*/30 * * * *", "Enabled": true }
```

Notes:

- Changes take effect on backend restart — the recurring job is registered at startup. Runtime reconfiguration is post-MVP.
- The cron expression is global per workspace; all repos share the same cadence (jobs themselves run per-repo). Per-repo cron customization is post-MVP.

## Project structure

```
archmind/
├── backend/            .NET 10 solution
│   ├── ArchMind.Api            HTTP API + MCP server host
│   ├── ArchMind.Core           Domain model, abstractions
│   ├── ArchMind.Infrastructure Postgres/AGE, Anthropic, persistence
│   └── ArchMind.Workers        Hangfire background jobs
├── admin-ui/           Next.js 15 admin UI
├── infra/              Infrastructure scripts (DB init, etc.)
├── docs/               Additional documentation
├── docker-compose.yml
└── .env.example
```

## Local dev (no Docker)

Postgres + AGE must still be running. The easiest path is to start only the database via Compose and run the app processes natively:

```bash
# Start just Postgres (with AGE) from the compose stack
docker compose up postgres -d

# Run the backend (API + MCP + Hangfire)
cd backend
dotnet run --project ArchMind.Api

# In a separate shell, run the admin UI
cd admin-ui
npm install
npm run dev
```

The backend listens on <http://localhost:5000> and the admin UI on <http://localhost:3000>, matching the Docker setup. Make sure your `.env` (or the backend's user secrets / env vars) points at `localhost:5432` for Postgres when running outside containers.

## Troubleshooting

- **`Port 5432 / 5000 / 3000 already in use`** — stop the conflicting local service, or change the host-side port mapping in `docker-compose.yml`.
- **AGE extension not loading** — confirm the `apache/age:PG16_latest` image was pulled successfully (`docker compose pull postgres`) and that `infra/postgres-init.sql` runs on first start. Init scripts only execute on an empty data volume; remove the Postgres volume (`docker compose down -v`) if you changed init SQL after the first boot. If AGE queries fail, run `./infra/age-smoke-test.sh` to verify setup.
- **Backend can't reach Postgres** — verify the Postgres container reports healthy via `docker compose ps`. The backend waits on the health check; if Postgres is unhealthy, inspect logs with `docker compose logs postgres`.
- **`graphify` is missing or broken in the backend container** — rebuild with `docker compose build --no-cache backend`. The Graphify CLI is installed from <https://github.com/safishamsi/graphify> during image build.
- **MCP client rejected with `401`** — the workspace API key was revoked or mistyped. Re-issue from `/workspaces/{slug}/settings/api-keys` and update the client config. Bearer keys are bound to a single workspace; using a key from workspace A against `/mcp/b` also returns 401.
- **MCP / SSE stream drops or hangs** — almost always a reverse proxy buffering its way into a deadlock. Confirm `proxy_buffering off` (nginx) or the equivalent for your terminator; full details in `docs/sse.md`.
- **Backend boots but admin UI shows "network error"** — check `Cors__AllowedOrigins__0` matches the exact admin UI origin (scheme + host + port). Each allowed origin is a separate indexed env var.

## Status

MVP under active development. Not for production use.

Sprint 1 currently delivers the admin UI shell, the Hangfire dashboard, and an empty workspace surface — most ingestion and query features are still being built.

See `docs/known-limitations.md` (placeholder — to be added) for a running list of caveats.
