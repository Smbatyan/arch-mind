using ArchMind.Core.Abstractions;
using ArchMind.Core.Models.Graph;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ArchMind.Infrastructure.Graph;

/// <summary>
/// BE-044: validates graph writes against
/// <see cref="GraphSchema"/> before they are turned into Cypher, and
/// queries the AGE catalog to detect drift between declared schema and
/// what AGE has actually materialised.
/// </summary>
/// <remarks>
/// <para><b>Policy summary.</b></para>
/// <list type="bullet">
///   <item>Unknown vertex / edge label → <c>IsValid = false</c> (write
///   must be skipped).</item>
///   <item>Missing required property → <c>IsValid = false</c>.</item>
///   <item>Edge from/to label outside spec → <c>IsValid = false</c>.</item>
///   <item>Unknown property key → warning only, prefixed with
///   <c>[warn]</c> in <see cref="SchemaValidationResult.Errors"/>.
///   Does not fail validation.</item>
/// </list>
/// <para><b>Drift check.</b></para>
/// Reads <c>ag_catalog.ag_label</c> rows scoped to the
/// <c>archmind_graph</c> graph and diffs the resulting set against
/// <see cref="GraphSchema"/>. Failures during the live check are
/// swallowed and logged; the returned report is
/// <see cref="SchemaDriftReport.Empty"/> on error so callers don't
/// crash a startup path.
/// </para>
/// </remarks>
internal sealed class GraphSchemaValidator : IGraphSchemaValidator
{
    private const string GraphName = "archmind_graph";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<GraphSchemaValidator> _logger;

    public GraphSchemaValidator(
        IDbConnectionFactory connectionFactory,
        ILogger<GraphSchemaValidator> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public SchemaValidationResult ValidateNode(
        string label,
        IReadOnlyDictionary<string, object?> properties)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return new SchemaValidationResult(false, new[] { "label is required" });
        }

        var spec = GraphSchema.FindNode(label);
        if (spec is null)
        {
            return new SchemaValidationResult(
                false,
                new[] { $"unknown vertex label '{label}'" });
        }

        var errors = new List<string>();

        foreach (var required in spec.Required)
        {
            if (!properties.TryGetValue(required, out var raw) || IsMissing(raw))
            {
                errors.Add($"missing required property '{required}' for label '{label}'");
            }
        }

        // Unknown properties are warnings — they don't break the write.
        foreach (var key in properties.Keys)
        {
            // Implementation-managed properties stamped by GraphWriter /
            // upstream pipeline. These are not declared per-label in
            // GraphSchema but are universally permitted.
            if (IsImplementationProperty(key)) continue;
            if (!spec.IsKnownProperty(key))
            {
                errors.Add($"[warn] unknown property '{key}' on label '{label}'");
            }
        }

        var isValid = !errors.Any(e => !e.StartsWith("[warn]", StringComparison.Ordinal));
        return new SchemaValidationResult(isValid, errors);
    }

    public SchemaValidationResult ValidateEdge(string label, string fromLabel, string toLabel)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return new SchemaValidationResult(false, new[] { "edge label is required" });
        }

        var spec = GraphSchema.FindEdge(label);
        if (spec is null)
        {
            return new SchemaValidationResult(
                false,
                new[] { $"unknown edge label '{label}'" });
        }

        var errors = new List<string>();

        // from/to labels may be unknown at the call site (e.g. when an
        // edge is being written before the endpoint vertices have a
        // declared label propagated to the caller). Treat empty as
        // "unknown" → allow but warn, so the writer's existing label
        // checks remain the primary guard.
        if (!string.IsNullOrWhiteSpace(fromLabel) && !spec.AcceptsFrom(fromLabel))
        {
            errors.Add(
                $"edge '{label}' does not accept from-label '{fromLabel}' " +
                $"(allowed: {string.Join(", ", spec.FromLabels)})");
        }
        else if (string.IsNullOrWhiteSpace(fromLabel))
        {
            errors.Add($"[warn] edge '{label}' validated without a from-label");
        }

        if (!string.IsNullOrWhiteSpace(toLabel) && !spec.AcceptsTo(toLabel))
        {
            errors.Add(
                $"edge '{label}' does not accept to-label '{toLabel}' " +
                $"(allowed: {string.Join(", ", spec.ToLabels)})");
        }
        else if (string.IsNullOrWhiteSpace(toLabel))
        {
            errors.Add($"[warn] edge '{label}' validated without a to-label");
        }

        var isValid = !errors.Any(e => !e.StartsWith("[warn]", StringComparison.Ordinal));
        return new SchemaValidationResult(isValid, errors);
    }

    public async Task<SchemaDriftReport> CheckLiveSchemaAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _connectionFactory.OpenAsync(ct).ConfigureAwait(false);

            var liveVertex = await QueryLabelsAsync(conn, kind: 'v', ct).ConfigureAwait(false);
            var liveEdge = await QueryLabelsAsync(conn, kind: 'e', ct).ConfigureAwait(false);

            var declaredVertex = GraphSchema.NodeLabels.Select(n => n.Label)
                .ToHashSet(StringComparer.Ordinal);
            var declaredEdge = GraphSchema.EdgeLabels.Select(e => e.Label)
                .Where(l => l != "*")
                .ToHashSet(StringComparer.Ordinal);

            var missingVertex = declaredVertex.Except(liveVertex, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var extraVertex = liveVertex.Except(declaredVertex, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var missingEdge = declaredEdge.Except(liveEdge, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var extraEdge = liveEdge.Except(declaredEdge, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();

            return new SchemaDriftReport(missingVertex, extraVertex, missingEdge, extraEdge);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live schema drift check failed; returning empty report.");
            return SchemaDriftReport.Empty;
        }
    }

    // ---- helpers ----

    private static async Task<HashSet<string>> QueryLabelsAsync(
        System.Data.Common.DbConnection conn, char kind, CancellationToken ct)
    {
        // ag_catalog.ag_label.graph is an oid pointing to ag_graph.
        // 'v' = vertex label, 'e' = edge label. AGE also stores a couple
        // of internal labels (e.g. "_ag_label_vertex" / "_ag_label_edge")
        // that are catalog scaffolding rather than user labels — filter
        // them out so they don't surface as "extra" drift.
        const string sql = @"
SELECT name
FROM ag_catalog.ag_label
WHERE graph = (SELECT graphid FROM ag_catalog.ag_graph WHERE name = @graph)
  AND kind = @kind
  AND name NOT LIKE '\_ag\_label\_%' ESCAPE '\';";

        var result = new HashSet<string>(StringComparer.Ordinal);

        await using var cmd = (NpgsqlCommand)conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@graph", GraphName);
        cmd.Parameters.AddWithValue("@kind", kind.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        _ => false,
    };

    /// <summary>
    /// Properties that are stamped by the writer or upstream pipeline,
    /// not declared per-label in <see cref="GraphSchema"/>. These should
    /// not produce "unknown property" warnings.
    /// </summary>
    private static bool IsImplementationProperty(string key) => key switch
    {
        "id" => true,
        "updated_at" => true,
        "created_at" => true,
        "last_extraction_id" => true,
        "repo_id" => true,
        "file_path" => true,
        _ => false,
    };
}
