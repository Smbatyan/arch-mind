# Known Limitations & Pre-Release Hardening

ArchMind is **MVP / testing-grade** software. It is intended for personal use,
internal dogfooding, and the validation experiment described in
`ArchMind-Validation-Plan.md`. This document inventories every deferred
concern so future-me does not forget what was traded away for speed.

Companion to `ArchMind-MVP-Technical.md` §20 ("Deferred to Pre-Release
Hardening") and `ArchMind-MVP-Tasks.md` DO-011.

---

## 1. What ArchMind Is NOT (Today)

- **Not production-hosted.** Runs only via local `docker compose`. No
  managed deployment, no CDN, no autoscaling, no failover.
- **Not multi-tenant secure.** Workspace isolation is application-level
  (`workspace_id` filter on every query). There is **no row-level
  security** in Postgres. A bug in a single endpoint can leak rows
  across workspaces.
- **Not authenticated for the public internet.** Custom email/password
  auth with bcrypt only. No SSO, no 2FA, no email verification, no
  password reset, no invite flow, no rate limiting on login.
- **Not encrypted at rest.** GitHub PATs (`repos.pat_token`), API keys,
  and clarification answers are stored as plain text in Postgres.
- **Not backed up.** No automated Postgres backup or restore process.
- **Not metered.** No Stripe, no usage caps, no quota enforcement.
- **Not observable.** No Prometheus / OTEL traces / centralized log
  shipping. Logs are JSON to stdout + a local file sink only.
- **Not tested.** No unit tests, no integration tests, no E2E suite.
  Verification happens via `/smoke` and manual UAT.
- **Not audited.** No penetration test, no SAST/DAST, no SOC2 evidence.

---

## 2. Open TODOs in the Codebase

Inventory of every `TODO` / `FIXME` comment as of 2026-05-27. Lines drift;
treat the file path + nearby context as the source of truth, not the
line number.

### Backend

| File | Line | Concern | Pre-release fix |
|---|---|---|---|
| `backend/ArchMind.Api/Endpoints/AuthEndpoints.cs` | 29 | Open registration | Switch to invite-only or admin-created users |
| `backend/ArchMind.Api/Endpoints/GraphEndpoints.cs` | 123 | Edge counts not exposed | Surface real counts via `IGraphReader` |
| `backend/ArchMind.Api/Endpoints/RepoEndpoints.cs` | 393 | Repo deletion leaks working dir | Enqueue cleanup job that removes `/var/archmind/.../{repo_id}/` |
| `backend/ArchMind.Core/Entities/Repo.cs` | 22 | `pat_token` stored as plain text | Encrypt with pgcrypto; consider external secrets manager for enterprise |
| `backend/ArchMind.Infrastructure/Graphify/GraphifyRunner.cs` | 165 | Graphify output schema assumed | Pin Graphify version + add output-format validation |
| `backend/ArchMind.Workers/Jobs/DiffScanJob.cs` | 112 | Huge diffs are capped | Batch into child jobs (Hangfire Pro continuation) |
| `backend/ArchMind.Workers/Jobs/DiffScanJob.cs` | 189 | Cross-file correlation not enqueued post-diff | Wire CrossFileCorrelationJob after every DiffScanJob |
| `backend/ArchMind.Workers/Jobs/LlmExtractionJob.cs` | 108 | 7 per-file LLM calls per file (cost) | Collapse into one mega-call returning all sections (measure quality first — Haiku may degrade) |
| `backend/ArchMind.Workers/Jobs/LlmExtractionJob.cs` | 211, 225 | Deterministic-Guid scheme for graph identity | Replace with proper graph identity lookup; `last_extraction_id` then becomes per-run, not per-file:hash |
| `backend/ArchMind.Workers/Pipelines/RepoScanPipeline.cs` | 173 | Synchronous file batch await | Move to Hangfire Pro continuations / proper batch await |
| `backend/ArchMind.Workers/Pipelines/RepoScanPipeline.cs` | 178 | Clarification engine not in pipeline | Wire ClarificationCandidateSweep after correlation |

### Admin UI

| File | Line | Concern | Pre-release fix |
|---|---|---|---|
| `admin-ui/app/(authed)/workspaces/[slug]/graph/graph-browser.tsx` | 616 | Node list truncated, no pagination | Add offset / cursor pagination |

---

## 3. Implementation Bugs Recently Patched (kept here as institutional memory)

These were live bugs in MVP code, now fixed. Documented so the same trap
does not get re-introduced.

- **LLM cache key collision across prompts** — `LlmExtractionJob` hashed
  `(fileContent | promptVersion | modelId)` and **omitted `promptId`**.
  All 7 per-file prompts shared one cache row; `IdentifyService` always
  ran first, then prompts 2–7 cache-hit on the same key and deserialised
  `ServiceExtraction` JSON into their own DTO types, producing silently
  null fields for endpoints / events / storage / conventions /
  integration contracts. Fixed by folding `promptId` into the cache key
  (commit `c7be4e5`). **Most pre-fix `file_extractions` rows still have
  null endpoints**; they self-heal as files change via DiffScanJob, or
  by bumping `ExtractionPromptLibrary.CurrentVersion` to force full
  re-extraction at the cost of ~6× per-file LLM calls × workspace size.
- **`ag_catalog.ag_graph.oid` does not exist** — the Apache AGE catalog
  table exposes the graph identity as `graphid`, not `oid`. The schema
  drift check silently returned empty reports. Fixed in commit
  `a27b547`.
- **Endpoint extraction missed `[Route(...)] + [HttpGet]`** — ASP.NET
  controllers with a class-level `[Route("api/foo")]` and a method
  attribute with no path argument (e.g. `[HttpGet]`) produced
  `endpoints: null`. Fixed by adding the route-combining rule to
  `ExtractionPromptLibrary` and bumping `CurrentVersion` to
  `2026-05-27/v3-route-combine`.

---

## 4. Architectural Bets That May Need Revisiting

These are not bugs; they are deliberate MVP choices whose downside is
still hypothetical. Watch them.

- **Single LLM provider (Anthropic only).** No OpenAI / self-hosted
  fallback. An Anthropic outage halts all extraction and the
  clarification engine.
- **Single graph store (Apache AGE).** AGE is young; we depend on its
  Cypher subset working as documented. If we hit a query AGE cannot
  express, the graph layer needs a Neo4j escape hatch.
- **Polling cadence is global (30 min default).** Per-workspace or
  per-repo cadence is in the data model but not enforced. Heavy
  workspaces share the same job slot as quiet ones.
- **Skill schema is server-authoritative.** No client-side skill
  versioning / draft state. A bad skill edit immediately affects all
  agents bound to that workspace.
- **`get_relevant_context` uses an ILIKE token search.** Postgres
  full-text or a vector index would be faster + smarter once corpus
  size grows.
- **Graphify is invoked as a subprocess.** Coupled to the Python
  package layout + filesystem output. Migrating to a library-style
  binding (or replacing extraction with a hand-rolled tree-sitter pass)
  is a known fork-in-the-road.

---

## 5. Pre-Release Hardening Roadmap (cross-ref to Technical doc §20)

Direct lift from `ArchMind-MVP-Technical.md` §20 so this file stands
alone when the technical doc is unavailable.

| Concern | Pre-release action |
|---|---|
| Auth | Replace custom auth with Logto or Supabase Auth; add SSO |
| Secrets storage | Encrypt PATs and API keys with pgcrypto; consider external secrets manager for enterprise |
| Production hosting | Choose Hetzner / managed PaaS / cloud provider; configure backups |
| Billing | Integrate Stripe |
| Email | Transactional provider for password reset, verification, digests |
| Webhook receiver | GitHub webhooks for sub-15-min change detection |
| Observability | Prometheus + Grafana; consider OpenTelemetry tracing |
| Centralized logging | Ship logs to Better Stack / Axiom / self-hosted Loki |
| Testing | Unit tests for extraction, MCP, clarification, skill matching; integration tests for critical flows |
| Backups | Automated Postgres backups with retention policy |
| Rate limiting | Per-workspace on MCP endpoint; per-user on admin API |
| Security audit | Penetration testing; SQL injection / XSS review |
| SOC2 roadmap | Begin documentation and process work as customer demand requires |
| Drift detection | Semantic resampling job to catch graph claims diverging from reality |
| Skill draft/published states | Add lifecycle to skills (draft → staging → published) |
| Multiple LLM providers | Add OpenAI / self-hosted model support for cost flexibility and resilience |
| BYOK | Allow enterprise customers to provide their own LLM API key |
| GitHub App | Migrate from PAT to GitHub App for cleaner permission model |
| Additional sources | Confluence, Jira, Notion, Linear, Slack |
| `archmind connect` CLI | Auto-configure Claude Code / Cursor / Windsurf |
| Skill marketplace | Pre-seeded skill library; community skill sharing |
| Multi-region | If demand requires |

---

## 6. What "Pre-Release" Means

ArchMind is **pre-release** until at least all of the following are
true:

1. Auth replaced with a hosted identity provider that handles email
   verification + password reset.
2. Every TODO in §2 of this doc is either fixed or has a tracked issue
   with a target milestone.
3. At least one external user has run ArchMind on their own codebase
   without help, for at least one week, and the experience did not
   require touching the database.
4. A backup + restore drill has been performed successfully on the
   primary database.
5. A documented security review (internal is fine; external is better)
   has been completed.

Until all five are true, this remains a pet project / validation tool
and should not be exposed beyond a small invited circle.

---

*Document version 1.0 — companion to `ArchMind-MVP-Technical.md` and
`ArchMind-MVP-Tasks.md` (DO-011).*
