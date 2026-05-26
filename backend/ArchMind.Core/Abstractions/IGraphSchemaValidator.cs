namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-044: validates graph write requests against the declared
/// <see cref="ArchMind.Core.Models.Graph.GraphSchema"/> before they are
/// turned into Cypher, and (separately) checks the live AGE catalog at
/// startup to surface schema drift.
///
/// Policy is intentionally lenient on the write path: unknown vertex
/// labels and required-property violations cause the write to be
/// rejected (returned as <see cref="SchemaValidationResult.IsValid"/> =
/// <c>false</c>), but unknown <i>properties</i> are warnings only —
/// callers may log and proceed, or skip the write. The extraction
/// pipeline is expected to log and skip on any failure rather than
/// throwing.
/// </summary>
public interface IGraphSchemaValidator
{
    /// <summary>
    /// Validates a vertex upsert. Returns <c>IsValid = false</c> if the
    /// label is unknown or any required property is missing/empty.
    /// Unknown property keys are emitted as <c>[warn]</c>-prefixed
    /// entries in <see cref="SchemaValidationResult.Errors"/> but do
    /// not flip <see cref="SchemaValidationResult.IsValid"/>.
    /// </summary>
    SchemaValidationResult ValidateNode(string label, IReadOnlyDictionary<string, object?> properties);

    /// <summary>
    /// Validates an edge upsert against the declared
    /// <see cref="ArchMind.Core.Models.Graph.EdgeLabelSpec"/>. Both the
    /// edge label and the from/to vertex labels must be known and
    /// permitted by the spec.
    /// </summary>
    SchemaValidationResult ValidateEdge(string label, string fromLabel, string toLabel);

    /// <summary>
    /// Queries the AGE catalog for the actual vertex / edge labels
    /// present in <c>archmind_graph</c> and compares them against
    /// <see cref="ArchMind.Core.Models.Graph.GraphSchema"/>. Used by
    /// the startup drift check and the
    /// <c>/api/workspaces/{slug}/graph/schema-report</c> endpoint.
    /// Never throws — failures are encoded as empty result lists so
    /// the caller can log a warning and move on.
    /// </summary>
    Task<SchemaDriftReport> CheckLiveSchemaAsync(CancellationToken ct);
}

/// <summary>
/// Outcome of a single write validation. When <see cref="IsValid"/> is
/// false the caller must skip the write. Entries in <see cref="Errors"/>
/// prefixed with <c>[warn]</c> are advisory only.
/// </summary>
public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static SchemaValidationResult Ok { get; } =
        new(true, Array.Empty<string>());
}

/// <summary>
/// Snapshot of declared-vs-live schema as seen from the AGE catalog.
/// "Extra" entries are labels AGE knows about that the declared schema
/// doesn't mention (probably stale or hand-rolled). "Missing" entries
/// are labels we declare but AGE hasn't materialised yet.
/// </summary>
public sealed record SchemaDriftReport(
    IReadOnlyList<string> MissingNodeLabels,
    IReadOnlyList<string> ExtraNodeLabels,
    IReadOnlyList<string> MissingEdgeLabels,
    IReadOnlyList<string> ExtraEdgeLabels)
{
    public bool HasDrift =>
        MissingNodeLabels.Count > 0 ||
        ExtraNodeLabels.Count > 0 ||
        MissingEdgeLabels.Count > 0 ||
        ExtraEdgeLabels.Count > 0;

    public static SchemaDriftReport Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());
}
