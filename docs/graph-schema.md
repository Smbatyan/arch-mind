# ArchMind Graph Schema

This graph lives in Apache AGE inside the Postgres `archmind_graph` graph. It
captures the architectural knowledge ArchMind extracts from a workspace:
services, the endpoints they expose, the events and topics they exchange, the
storage they own, the conventions they follow, and the people and teams behind
them.

The schema is provisioned by `infra/postgres-init.sql` (labels + indexes). C#
graph writers and readers live in `ArchMind.Infrastructure` (see BE-021 /
BE-022).

## Common properties

Every node carries these properties (enforced by the writers, not by AGE):

| Property              | Type   | Description                                               |
| --------------------- | ------ | --------------------------------------------------------- |
| `workspace_id`        | UUID   | Tenant id. **Every** read MUST filter on this.            |
| `created_at`          | ISO ts | First time the node was extracted.                        |
| `updated_at`          | ISO ts | Last time any property changed.                           |
| `last_extraction_id`  | UUID   | The extraction run that most recently touched this node.  |

## Node types

| Label        | Key properties                                                                       | Description                                                                  |
| ------------ | ------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------- |
| `Service`    | `name`, `purpose`, `repo_id`, `root_path`, `tech_stack`                              | A deployable unit owned by a team.                                           |
| `Endpoint`   | `path`, `method`, `protocol`, `auth`                                                 | An HTTP/gRPC endpoint exposed by a `Service`.                                |
| `Event`      | `name`, `schema_ref`, `version`                                                      | A domain event published or consumed by services.                            |
| `Topic`      | `name`, `broker`, `partitions`                                                       | A message-broker topic / stream that carries events.                         |
| `Storage`    | `name`, `kind`, `engine`, `schema_ref`                                               | A datastore (DB, cache, blob bucket, etc.) owned by a service.               |
| `Dependency` | `name`, `kind`, `version`, `source`                                                  | An external library or service the codebase relies on.                       |
| `Convention` | `name`, `summary`, `rule`                                                            | A coding / architectural convention extracted from docs or repeated patterns.|
| `Capability` | `name`, `description`                                                                | A business capability offered by one or more services.                       |
| `Skill`      | `name`, `category`                                                                   | A technical skill (language, framework, tool).                               |
| `Team`       | `name`, `slug`, `contact`                                                            | A team that owns services and capabilities.                                  |

Properties beyond the key set may exist — writers are free to add them as
extraction improves; readers should treat unknown properties as opaque.

## Edge types

All edges also carry `workspace_id`, `created_at`, `updated_at`, and
`last_extraction_id`.

| Label             | From → To                       | Description                                              |
| ----------------- | ------------------------------- | -------------------------------------------------------- |
| `PUBLISHES`       | `Service` → `Event`             | Service emits this event.                                |
| `CONSUMES`        | `Service` → `Event`             | Service handles / subscribes to this event.              |
| `OWNS`            | `Service` → `Storage`           | Service is the system-of-record for this storage.        |
| `READS`           | `Service` → `Storage`           | Service reads from this storage (without owning it).     |
| `EXPOSES`         | `Service` → `Endpoint`          | Service exposes this endpoint.                           |
| `CALLS`           | `Service` → `Endpoint`          | Service calls another service's endpoint.                |
| `PUBLISHED_TO`    | `Event` → `Topic`               | Event is published onto this topic.                      |
| `CONSUMED_FROM`   | `Event` → `Topic`               | Event is consumed from this topic.                       |
| `DEPENDS_ON`      | `Service` → `Dependency`        | Service depends on a library or external system.         |
| `FOLLOWS`         | `Service` → `Convention`        | Service is observed to follow this convention.           |
| `USES_CAPABILITY` | `Service` → `Capability`        | Service implements / contributes to this capability.     |
| `APPLIES_TO`      | `Convention` → `Service`        | Convention is declared to apply to this service.         |
| `OWNED_BY`        | `Service` / `Capability` → `Team` | Ownership pointer to a team.                           |

## Traversal patterns

AGE queries are wrapped in a `cypher(...)` table function. Each example below
is copy-pasteable into psql.

All services in a workspace:

```sql
SELECT * FROM cypher('archmind_graph', $$
    MATCH (s:Service {workspace_id: '00000000-0000-0000-0000-000000000000'})
    RETURN s
$$) AS (result agtype);
```

Who publishes a given event:

```sql
SELECT * FROM cypher('archmind_graph', $$
    MATCH (s:Service)-[:PUBLISHES]->(e:Event {name: 'OrderPlaced'})
    WHERE s.workspace_id = '00000000-0000-0000-0000-000000000000'
      AND e.workspace_id = '00000000-0000-0000-0000-000000000000'
    RETURN s
$$) AS (result agtype);
```

External dependencies of a service:

```sql
SELECT * FROM cypher('archmind_graph', $$
    MATCH (s:Service {name: 'checkout'})-[:DEPENDS_ON]->(d:Dependency)
    WHERE s.workspace_id = '00000000-0000-0000-0000-000000000000'
    RETURN d
$$) AS (result agtype);
```

Endpoint callers (who calls `payments`?):

```sql
SELECT * FROM cypher('archmind_graph', $$
    MATCH (caller:Service)-[:CALLS]->(:Endpoint)<-[:EXPOSES]-(callee:Service {name: 'payments'})
    WHERE caller.workspace_id = '00000000-0000-0000-0000-000000000000'
    RETURN caller
$$) AS (result agtype);
```

## Indexes

`infra/postgres-init.sql` creates an expression index on the `workspace_id`
property for every vertex label (`ix_<label>_workspace`). This keeps the
workspace filter — required on every query — cheap.

If you find workspace-scoped queries doing sequential scans on a fresh
deployment, check:

1. The label table exists (`SELECT * FROM ag_catalog.ag_label WHERE name = '...'`).
2. The index exists (`\d archmind_graph."<Label>"` in psql).
3. The AGE property-access expression in the index matches your AGE version
   (see the comment block in `postgres-init.sql`).

Additional property indexes (e.g. `Service.name`, `Event.name`) can be added
the same way as query patterns stabilize.

## Multi-tenancy

Every node and every edge carries `workspace_id`. This is non-negotiable.

- **Writers** must set `workspace_id` on every node / edge they create.
- **Readers** must include `WHERE n.workspace_id = $1` in every `MATCH` clause.
- There is no global "all workspaces" query path in MVP.

This mirrors the Postgres-side contract enforced by
`WorkspaceScopedRepositoryBase<T>` (see `docs/architecture.md`).
