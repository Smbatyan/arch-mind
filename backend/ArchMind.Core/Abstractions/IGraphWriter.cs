namespace ArchMind.Core.Abstractions;

/// <summary>
/// Write-side facade over the Apache AGE knowledge graph (graph name
/// <c>archmind_graph</c>). Implemented in <c>ArchMind.Infrastructure</c> using
/// Dapper over Cypher-in-SQL. Reads live behind a separate
/// <c>IGraphReader</c> abstraction (BE-022).
/// </summary>
/// <remarks>
/// Multi-tenancy invariant: every graph vertex MUST carry a
/// <c>workspace_id</c> property, and every matcher used by this writer MUST
/// constrain on it. Implementations enforce this at runtime — calls missing
/// <c>workspace_id</c> are rejected with <see cref="ArgumentException"/>.
/// </remarks>
public interface IGraphWriter
{
    /// <summary>
    /// MERGE a vertex by (label, id). Properties are merged via <c>SET n +=
    /// $props</c>; <c>updated_at</c> is stamped server-side. If
    /// <paramref name="spec"/>'s <c>LastExtractionId</c> is provided it is set
    /// as <c>n.last_extraction_id</c>.
    /// </summary>
    /// <returns>The node's stable id (echo of <c>spec.Id</c>).</returns>
    Task<Guid> UpsertNodeAsync(GraphNodeSpec spec, CancellationToken ct = default);

    /// <summary>
    /// MERGE an edge between two existing vertices (matched by id +
    /// workspace_id). Properties are merged via <c>SET r += $props</c>.
    /// </summary>
    Task UpsertEdgeAsync(GraphEdgeSpec spec, CancellationToken ct = default);

    /// <summary>
    /// DETACH DELETE the vertex with the given (workspace, label, id).
    /// No-op if it doesn't exist.
    /// </summary>
    Task RemoveNodeAsync(Guid workspaceId, string label, Guid nodeId, CancellationToken ct = default);

    /// <summary>
    /// DELETE a specific edge by (workspace, edge label, source id, target id).
    /// </summary>
    Task RemoveEdgeAsync(Guid workspaceId, string edgeLabel, Guid sourceId, Guid targetId, CancellationToken ct = default);

    /// <summary>
    /// Delete every vertex with <c>file_path = filePath</c> and
    /// <c>last_extraction_id != currentExtractionId</c> for the given workspace
    /// and repo. Used at the end of a diff scan to evict nodes whose source
    /// constructs disappeared from the file.
    /// </summary>
    /// <returns>Number of orphan vertices removed.</returns>
    Task<int> RemoveOrphansForFileAsync(Guid workspaceId, Guid repoId, string filePath, CancellationToken ct = default);

    /// <summary>
    /// Open a session bound to a single connection + transaction. The callback
    /// receives an <see cref="IGraphWriteSession"/> whose operations reuse the
    /// underlying connection. Commits on success; rolls back on exception.
    /// </summary>
    /// <returns>Number of operations executed against the session.</returns>
    Task<int> ExecuteInTransactionAsync(Func<IGraphWriteSession, Task> work, CancellationToken ct = default);
}

/// <summary>
/// Spec for a graph vertex upsert.
/// </summary>
public sealed record GraphNodeSpec(
    Guid WorkspaceId,
    string Label,
    Guid Id,
    IReadOnlyDictionary<string, object?> Properties,
    Guid? LastExtractionId = null);

/// <summary>
/// Spec for a graph edge upsert.
/// </summary>
public sealed record GraphEdgeSpec(
    Guid WorkspaceId,
    string Label,
    Guid SourceId,
    Guid TargetId,
    IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>
/// Per-transaction session exposed via
/// <see cref="IGraphWriter.ExecuteInTransactionAsync"/>. Same multi-tenancy and
/// allowlist rules apply.
/// </summary>
public interface IGraphWriteSession
{
    Task UpsertNodeAsync(GraphNodeSpec spec, CancellationToken ct = default);
    Task UpsertEdgeAsync(GraphEdgeSpec spec, CancellationToken ct = default);
}
