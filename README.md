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
# Edit .env and set ANTHROPIC_API_KEY (plus any other secrets/ports you want to change)

# 3. Start everything
docker compose up -d
```

Once the stack is up:

- Admin UI: <http://localhost:3000>
- Hangfire dashboard: <http://localhost:5000/hangfire>
- MCP endpoint pattern: `http://localhost:5000/mcp/{workspace_slug}`

Check service health with `docker compose ps` and tail logs with `docker compose logs -f`.

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

## Status

MVP under active development. Not for production use.

Sprint 1 currently delivers the admin UI shell, the Hangfire dashboard, and an empty workspace surface — most ingestion and query features are still being built.

See `docs/known-limitations.md` (placeholder — to be added) for a running list of caveats.
