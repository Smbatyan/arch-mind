# ArchMind Architecture Notes

## Multi-tenancy contract

Every domain table in ArchMind includes `workspace_id UUID NOT NULL` with a foreign key
to `workspaces(id)`. This is the single most important architectural rule.

Repositories **MUST** filter by `workspace_id` on every read and write.
The base class
[`WorkspaceScopedRepositoryBase<T>`](../backend/ArchMind.Infrastructure/Repositories/WorkspaceScopedRepositoryBase.cs)
enforces this:

- Entities scoped to a workspace implement
  [`IWorkspaceScoped`](../backend/ArchMind.Core/Abstractions/IWorkspaceScoped.cs).
- The protected `Query(Guid workspaceId)` method is the only sanctioned way to read.
- `QueryNoFilter()` throws `InvalidOperationException` by default. Subclasses
  must explicitly override it with a documented reason — no MVP code does.

### Exemptions

- `workspaces` — *is* the tenant; no `workspace_id` column.
- `users` — global users table. A single user may belong to multiple workspaces.
  Access is always scoped via `workspace_members`.
