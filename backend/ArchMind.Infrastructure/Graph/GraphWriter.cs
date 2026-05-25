using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchMind.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ArchMind.Infrastructure.Graph;

/// <summary>
/// Dapper-backed implementation of <see cref="IGraphWriter"/> for Apache AGE.
/// Every Cypher block is composed as a literal string (label interpolated from
/// the <see cref="GraphLabels"/> allowlist) with property maps passed as
/// parameters via the AGE positional-parameter protocol.
/// </summary>
/// <remarks>
/// <para><b>AGE parameter binding (the load-bearing detail).</b></para>
/// AGE's <c>cypher()</c> SQL function accepts:
/// <code>cypher(graph_name text, query_string cstring, params agtype DEFAULT NULL)</code>
/// where <c>params</c> is an agtype map whose keys correspond to Cypher
/// parameters referenced as <c>$key</c> inside the query string. Inside Cypher,
/// you may not use bind variables prefixed with <c>:</c> — only <c>$name</c>
/// works, and only when the outer SQL passes the params map.
/// <para>
/// Npgsql cannot bind a parameter to the SQL function's <c>params</c> slot if
/// it appears <i>inside</i> the dollar-quoted Cypher block (the dollar quoting
/// hides the <c>$1</c>). So we pass the params map by interpolating an agtype
/// literal AFTER the dollar-quoted Cypher: <c>cypher('g', $$ ... $$,
/// '{"k":"v"}'::agtype)</c>. The JSON we interpolate is built from
/// <see cref="JsonSerializer"/> on a sanitized dictionary, which removes the
/// injection surface that bare string concatenation would expose.
/// </para>
/// <para>
/// If a future AGE revision allows binding the agtype map through a real
/// Npgsql parameter (e.g. via custom type handler), swap the JSON
/// interpolation in <see cref="BuildParamsLiteral"/> for a real parameter —
/// the call sites need no other changes.
/// </para>
/// <para><b>Injection defences.</b></para>
/// <list type="number">
///   <item>Labels validated against <see cref="GraphLabels"/>.</item>
///   <item>Property keys validated by <see cref="PropertyKeyRegex"/>.</item>
///   <item>Property values JSON-serialised, then escaped for single-quote
///   contexts; Guids stringified; timestamps stamped server-side via
///   <c>now()</c>.</item>
///   <item><c>workspace_id</c> required in every matcher.</item>
/// </list>
/// </remarks>
internal sealed class GraphWriter : IGraphWriter
{
    private const string GraphName = "archmind_graph";
    private const string WorkspaceIdKey = "workspace_id";

    // Cypher identifiers and property keys must match this — same shape AGE
    // accepts but stricter (lowercase only, no leading digit). Keep this
    // tighter than AGE's parser to defend in depth.
    private static readonly Regex PropertyKeyRegex =
        new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<GraphWriter> _logger;

    public GraphWriter(IDbConnectionFactory connectionFactory, ILogger<GraphWriter> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Guid> UpsertNodeAsync(GraphNodeSpec spec, CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await UpsertNodeCoreAsync(conn, transaction: null, spec, ct).ConfigureAwait(false);
        return spec.Id;
    }

    public async Task UpsertEdgeAsync(GraphEdgeSpec spec, CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await UpsertEdgeCoreAsync(conn, transaction: null, spec, ct).ConfigureAwait(false);
    }

    public async Task RemoveNodeAsync(Guid workspaceId, string label, Guid nodeId, CancellationToken ct = default)
    {
        ValidateVertexLabel(label);
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("workspace_id is required.", nameof(workspaceId));

        var sql = $@"
SELECT * FROM cypher('{GraphName}', $$
    MATCH (n:{label} {{ id: $id, workspace_id: $ws }})
    DETACH DELETE n
$$, {BuildParamsLiteral(new Dictionary<string, object?>
{
    ["id"] = nodeId.ToString(),
    ["ws"] = workspaceId.ToString(),
})}) AS (a agtype);";

        await using var conn = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteCypherAsync(conn, transaction: null, sql, ct).ConfigureAwait(false);
    }

    public async Task RemoveEdgeAsync(Guid workspaceId, string edgeLabel, Guid sourceId, Guid targetId, CancellationToken ct = default)
    {
        ValidateEdgeLabel(edgeLabel);
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("workspace_id is required.", nameof(workspaceId));

        var sql = $@"
SELECT * FROM cypher('{GraphName}', $$
    MATCH (a {{ id: $sid, workspace_id: $ws }})-[r:{edgeLabel}]->(b {{ id: $tid, workspace_id: $ws }})
    DELETE r
$$, {BuildParamsLiteral(new Dictionary<string, object?>
{
    ["sid"] = sourceId.ToString(),
    ["tid"] = targetId.ToString(),
    ["ws"] = workspaceId.ToString(),
})}) AS (a agtype);";

        await using var conn = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteCypherAsync(conn, transaction: null, sql, ct).ConfigureAwait(false);
    }

    public async Task<int> RemoveOrphansForFileAsync(Guid workspaceId, Guid repoId, string filePath, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("workspace_id is required.", nameof(workspaceId));
        if (repoId == Guid.Empty)
            throw new ArgumentException("repo_id is required.", nameof(repoId));
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("file_path is required.", nameof(filePath));

        // Two passes inside one Cypher statement: WITH-collect then DETACH
        // DELETE so we get a deterministic count. The current extraction id
        // for a (workspace, repo, file) is whatever's currently stored on
        // surviving nodes from the latest extraction run; orphans are nodes
        // whose last_extraction_id differs from the maximum.
        var sql = $@"
SELECT count FROM cypher('{GraphName}', $$
    MATCH (n {{ workspace_id: $ws, repo_id: $repo, file_path: $fp }})
    WITH max(n.last_extraction_id) AS current_ext
    MATCH (n {{ workspace_id: $ws, repo_id: $repo, file_path: $fp }})
    WHERE n.last_extraction_id <> current_ext OR n.last_extraction_id IS NULL
    WITH collect(n) AS orphans
    FOREACH (x IN orphans | DETACH DELETE x)
    RETURN size(orphans)
$$, {BuildParamsLiteral(new Dictionary<string, object?>
{
    ["ws"] = workspaceId.ToString(),
    ["repo"] = repoId.ToString(),
    ["fp"] = filePath,
})}) AS (count agtype);";

        await using var conn = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = (NpgsqlCommand)conn.CreateCommand();
        cmd.CommandText = sql;

        // agtype scalars come back as strings shaped like "5" — parse defensively.
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return ParseAgtypeInt(scalar);
    }

    public async Task<int> ExecuteInTransactionAsync(Func<IGraphWriteSession, Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var conn = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var session = new TransactionalSession(this, conn, tx);
        try
        {
            await work(session).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return session.OperationCount;
        }
        catch
        {
            try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
            catch (Exception rollbackEx)
            {
                _logger.LogWarning(rollbackEx, "Graph transaction rollback failed.");
            }
            throw;
        }
    }

    // ---- shared cores (used by both connection-per-call and session paths) ----

    private async Task UpsertNodeCoreAsync(DbConnection conn, DbTransaction? transaction, GraphNodeSpec spec, CancellationToken ct)
    {
        ValidateVertexLabel(spec.Label);
        if (spec.WorkspaceId == Guid.Empty)
            throw new ArgumentException("WorkspaceId is required.", nameof(spec));
        if (spec.Id == Guid.Empty)
            throw new ArgumentException("Node id is required.", nameof(spec));

        // Merge user-supplied props with the required workspace_id; the input
        // map MAY also carry workspace_id, but it must match the spec value.
        var props = SanitizeProperties(spec.Properties);
        if (props.TryGetValue(WorkspaceIdKey, out var existingWs))
        {
            if (existingWs?.ToString() != spec.WorkspaceId.ToString())
                throw new ArgumentException(
                    "Properties.workspace_id must match spec.WorkspaceId.", nameof(spec));
        }
        else
        {
            props[WorkspaceIdKey] = spec.WorkspaceId.ToString();
        }

        if (spec.LastExtractionId is { } extId)
            props["last_extraction_id"] = extId.ToString();

        var sql = $@"
SELECT * FROM cypher('{GraphName}', $$
    MERGE (n:{spec.Label} {{ id: $id, workspace_id: $ws }})
    SET n += $props,
        n.updated_at = timestamp()
    RETURN n
$$, {BuildParamsLiteral(new Dictionary<string, object?>
{
    ["id"] = spec.Id.ToString(),
    ["ws"] = spec.WorkspaceId.ToString(),
    ["props"] = props,
})}) AS (n agtype);";

        await ExecuteCypherAsync(conn, transaction, sql, ct).ConfigureAwait(false);
    }

    private async Task UpsertEdgeCoreAsync(DbConnection conn, DbTransaction? transaction, GraphEdgeSpec spec, CancellationToken ct)
    {
        ValidateEdgeLabel(spec.Label);
        if (spec.WorkspaceId == Guid.Empty)
            throw new ArgumentException("WorkspaceId is required.", nameof(spec));
        if (spec.SourceId == Guid.Empty || spec.TargetId == Guid.Empty)
            throw new ArgumentException("Source and target ids are required.", nameof(spec));

        var props = spec.Properties is null
            ? new Dictionary<string, object?>()
            : SanitizeProperties(spec.Properties);

        var sql = $@"
SELECT * FROM cypher('{GraphName}', $$
    MATCH (a {{ id: $sid, workspace_id: $ws }}),
          (b {{ id: $tid, workspace_id: $ws }})
    MERGE (a)-[r:{spec.Label}]->(b)
    SET r += $props,
        r.updated_at = timestamp()
    RETURN r
$$, {BuildParamsLiteral(new Dictionary<string, object?>
{
    ["sid"] = spec.SourceId.ToString(),
    ["tid"] = spec.TargetId.ToString(),
    ["ws"] = spec.WorkspaceId.ToString(),
    ["props"] = props,
})}) AS (r agtype);";

        await ExecuteCypherAsync(conn, transaction, sql, ct).ConfigureAwait(false);
    }

    // ---- helpers ----

    private static void ValidateVertexLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label is required.", nameof(label));
        if (!GraphLabels.IsVertex(label))
            throw new ArgumentException(
                $"Unknown vertex label '{label}'. Allowed: {string.Join(", ", GraphLabels.Vertex)}.",
                nameof(label));
    }

    private static void ValidateEdgeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label is required.", nameof(label));
        if (!GraphLabels.IsEdge(label))
            throw new ArgumentException(
                $"Unknown edge label '{label}'. Allowed: {string.Join(", ", GraphLabels.Edge)}.",
                nameof(label));
    }

    private static Dictionary<string, object?> SanitizeProperties(IReadOnlyDictionary<string, object?> input)
    {
        var clean = new Dictionary<string, object?>(input.Count, StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            if (!PropertyKeyRegex.IsMatch(key))
            {
                throw new ArgumentException(
                    $"Invalid property key '{key}'. Keys must match [a-z_][a-z0-9_]*.",
                    nameof(input));
            }
            clean[key] = NormalizeValue(value);
        }
        return clean;
    }

    /// <summary>
    /// Normalises a value into something <see cref="JsonSerializer"/> renders
    /// as a valid agtype scalar/array/object. Guids become strings;
    /// DateTime/DateTimeOffset become ISO-8601 strings; nested dictionaries
    /// and enumerables pass through.
    /// </summary>
    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            null => null,
            Guid g => g.ToString(),
            DateTime dt => dt.ToUniversalTime().ToString("O"),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O"),
            _ => value,
        };
    }

    /// <summary>
    /// Renders the parameter map as an inline <c>'{...}'::agtype</c> literal.
    /// </summary>
    /// <remarks>
    /// Why not a real Npgsql parameter? Npgsql can't currently bind to the
    /// third positional parameter of the <c>cypher()</c> SQL function when
    /// the second argument is a dollar-quoted Cypher block — the JSON literal
    /// approach is what the AGE docs themselves use in examples. We mitigate
    /// the injection risk by serialising with <see cref="JsonSerializer"/>
    /// (which escapes embedded quotes/backslashes correctly) and by gating
    /// keys through <see cref="PropertyKeyRegex"/>. The SQL single-quote
    /// escaping is a final defence: any <c>'</c> inside the JSON gets doubled.
    /// </remarks>
    private static string BuildParamsLiteral(IDictionary<string, object?> parameters)
    {
        var json = JsonSerializer.Serialize(parameters, JsonOpts);
        var escaped = json.Replace("'", "''");
        return $"'{escaped}'::agtype";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private static async Task ExecuteCypherAsync(DbConnection conn, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (transaction is not null) cmd.Transaction = transaction;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static int ParseAgtypeInt(object? scalar)
    {
        if (scalar is null || scalar is DBNull) return 0;
        var s = scalar.ToString();
        if (string.IsNullOrEmpty(s)) return 0;
        // AGE may return "5" or "5::int" depending on version; strip type tag.
        var bar = s.IndexOf("::", StringComparison.Ordinal);
        if (bar >= 0) s = s[..bar];
        return int.TryParse(s, out var v) ? v : 0;
    }

    // ---- session (transactional) ----

    private sealed class TransactionalSession : IGraphWriteSession
    {
        private readonly GraphWriter _owner;
        private readonly DbConnection _conn;
        private readonly DbTransaction _tx;
        public int OperationCount { get; private set; }

        public TransactionalSession(GraphWriter owner, DbConnection conn, DbTransaction tx)
        {
            _owner = owner;
            _conn = conn;
            _tx = tx;
        }

        public async Task UpsertNodeAsync(GraphNodeSpec spec, CancellationToken ct = default)
        {
            await _owner.UpsertNodeCoreAsync(_conn, _tx, spec, ct).ConfigureAwait(false);
            OperationCount++;
        }

        public async Task UpsertEdgeAsync(GraphEdgeSpec spec, CancellationToken ct = default)
        {
            await _owner.UpsertEdgeCoreAsync(_conn, _tx, spec, ct).ConfigureAwait(false);
            OperationCount++;
        }
    }
}
