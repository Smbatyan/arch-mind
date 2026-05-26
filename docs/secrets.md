# Secrets management

This document lists every secret ArchMind reads at runtime, how to rotate each,
and guidance for production hardening. See `.env.example` for the canonical list
of variables and their defaults.

## Required vs optional

| Variable                       | Required | Used by   | Notes |
| ------------------------------ | -------- | --------- | ----- |
| `POSTGRES_USER`                | Yes      | postgres  | Owns the workspace schema and AGE graphs. |
| `POSTGRES_PASSWORD`            | Yes      | postgres + backend | Must match the password embedded in `ConnectionStrings__Default`. |
| `POSTGRES_DB`                  | Yes      | postgres  | Database name. |
| `ConnectionStrings__Default`   | Yes      | backend   | Full Npgsql connection string. |
| `Anthropic__ApiKey`            | Yes      | backend   | Used for ingestion (Haiku 4.5) and correlation (Sonnet 4.6). |
| `Anthropic__ExtractionModel`   | No       | backend   | Override default extraction model. |
| `Anthropic__CorrelationModel`  | No       | backend   | Override default correlation model. |
| `GitHub__AppId`                | Optional | backend   | Required to scan private GitHub repos. Blank = public-only. |
| `GitHub__InstallationId`       | Optional | backend   | Pair with `AppId`. |
| `GitHub__PrivateKeyPath`       | Optional | backend   | Path inside the backend container to the GitHub App PEM. |
| `Auth__RequireHttps`           | Yes      | backend   | `false` for local dev, `true` in production behind HTTPS. |
| `Cors__AllowedOrigins__0..n`   | Yes      | backend   | One env var per allowed admin UI origin. |
| `Logging__FilePath`            | No       | backend   | Defaults to `/var/log/archmind`. |
| `Polling__IntervalMinutes`     | No       | backend   | Defaults to 15. |
| `NEXT_PUBLIC_API_URL`          | Yes      | admin-ui  | Baked into the Next.js client bundle at build time. |
| `AdminSeed__Email`             | No       | backend   | Bootstrap admin email; only used on empty DB. |
| `AdminSeed__Password`          | No       | backend   | Bootstrap admin password; change immediately after first login. |
| `ASPNETCORE_ENVIRONMENT`       | No       | backend   | `Production` disables automatic EF migrations. |

## Rotation

### `POSTGRES_PASSWORD`
1. Stop the stack: `docker compose down`.
2. Edit `.env` to the new password.
3. Update `ConnectionStrings__Default` to match.
4. Bring it back up: `docker compose up -d`.

   If the Postgres volume is already initialized with the old password, you
   must either run `ALTER USER archmind WITH PASSWORD '...';` against the
   running database before changing `.env`, or recreate the volume (data loss).
   Prefer the in-place `ALTER USER` path for any DB that already holds graphs
   you care about.

### `Anthropic__ApiKey`
1. Edit `.env` with the new key (or rotate via your secrets store).
2. `docker compose restart backend`.

   No persistence concerns — keys are only read at process start and per
   request. Compromised keys should be revoked in the Anthropic console
   immediately, separately from this rotation.

### `GitHub__PrivateKeyPath`
1. Drop the new PEM at the bind-mounted path on the host.
2. `docker compose restart backend`.

   The path itself doesn't change; only the file contents swap. If you do
   change the path, update `.env` and restart.

### `AdminSeed__Password`
Used only when the users table is empty. Once an admin exists, change the
password from the admin UI (`/settings/account`). Editing `.env` after
seeding has no effect.

### `ConnectionStrings__Default`
Treat as derived from `POSTGRES_PASSWORD`. Rotate together.

## Production hardening

- **Do not bake secrets into images.** The provided `Dockerfile`s never
  `COPY .env`. Pass secrets only via environment, bind mounts, or a secret
  manager.
- **Prefer Docker Secrets or an external KV store** (HashiCorp Vault, AWS
  Secrets Manager, Azure Key Vault, GCP Secret Manager) over `.env` for
  any deployment with more than one operator. Inject the resolved values
  into the container environment at start time.
- **Bind-mount the GitHub App PEM** rather than passing the key body via
  env var. Use file permissions `0400` and an owner the backend container
  user can read.
- **Lock down `.env`** if you must use it: `chmod 600 .env` and store it on
  the deployment host outside any git checkout.
- **Restrict Postgres port exposure.** Remove the `5432:5432` host
  publication in `docker-compose.yml` for production; the backend reaches
  Postgres over the internal `archmind` network.
- **Always run behind HTTPS in production** and set `Auth__RequireHttps=true`
  so the auth cookie carries the `Secure` flag.

## Threat model notes

- **Secrets at rest in `.env`.** Anyone with read access to the file or the
  host filesystem can recover every secret. Treat `.env` as a credential
  and protect it with the same care.
- **Secrets in container env.** `docker inspect <container>` reveals every
  environment variable. Anyone in the `docker` group on the host effectively
  has root, so restrict that group aggressively.
- **Secrets in logs.** Serilog destructuring policies in
  `ArchMind.Api/Logging` are configured to redact common secret-shaped
  fields (Authorization headers, API keys, connection strings). When adding
  new structured log calls, never pass `Anthropic__ApiKey`,
  `ConnectionStrings__Default`, bearer tokens, or PEM bodies as templated
  parameters — even at Debug level. Confirm new log statements through
  the redaction policy before merging.
- **Workspace API keys.** Stored hashed in the database (see `docs/architecture.md`).
  Only the prefix is shown in the admin UI after creation; full keys are
  displayed exactly once on the create page. Treat user-issued workspace
  keys the same as personal access tokens.
