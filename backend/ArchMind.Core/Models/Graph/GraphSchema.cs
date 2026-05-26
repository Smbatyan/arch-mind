namespace ArchMind.Core.Models.Graph;

/// <summary>
/// BE-044: Declarative source-of-truth for the ArchMind graph schema —
/// vertex labels with required/optional properties, and edge labels with
/// allowed from/to vertex labels.
///
/// Lives in <c>ArchMind.Core</c> so both <c>ArchMind.Infrastructure</c>
/// (GraphWriter / GraphLabels / GraphSchemaValidator) and the API layer
/// (schema-report endpoint) can reference it without a friend-assembly
/// dependency.
///
/// This is the canonical declaration. The infra-internal
/// <c>GraphLabels</c> allowlist is now derived from these specs so the
/// two can't drift. The AGE-side label catalog
/// (<c>infra/postgres-init.sql</c>) is intentionally not coupled to this
/// list at build time — the runtime drift check
/// (<see cref="ArchMind.Core.Abstractions.IGraphSchemaValidator.CheckLiveSchemaAsync"/>)
/// surfaces mismatches as a warning so out-of-band schema changes are
/// observable without crashing.
/// </summary>
public static class GraphSchema
{
    public static readonly IReadOnlyList<NodeLabelSpec> NodeLabels = new[]
    {
        new NodeLabelSpec("Service",    Required: new[] { "name", "workspace_id" },        Optional: new[] { "language", "framework", "description", "source_files" }),
        new NodeLabelSpec("Endpoint",   Required: new[] { "method", "path", "workspace_id" }, Optional: new[] { "service_name", "status_codes" }),
        new NodeLabelSpec("Database",   Required: new[] { "name", "workspace_id" },        Optional: new[] { "kind", "tech" }),
        new NodeLabelSpec("Queue",      Required: new[] { "name", "workspace_id" },        Optional: new[] { "kind" }),
        new NodeLabelSpec("Event",      Required: new[] { "name", "workspace_id" },        Optional: new[] { "payload_summary" }),
        new NodeLabelSpec("Concept",    Required: new[] { "name", "workspace_id" },        Optional: new[] { "description" }),
        new NodeLabelSpec("File",       Required: new[] { "path", "workspace_id" },        Optional: new[] { "language" }),
        new NodeLabelSpec("Convention", Required: new[] { "name", "workspace_id" },        Optional: new[] { "description" }),
        new NodeLabelSpec("Capability", Required: new[] { "name", "workspace_id" },        Optional: new[] { "description" }),
        new NodeLabelSpec("Storage",    Required: new[] { "name", "workspace_id" },        Optional: new[] { "kind" }),
    };

    public static readonly IReadOnlyList<EdgeLabelSpec> EdgeLabels = new[]
    {
        new EdgeLabelSpec("CALLS",          FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Endpoint" }),
        new EdgeLabelSpec("EXPOSES",        FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Endpoint" }),
        new EdgeLabelSpec("READS_FROM",     FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Database", "Storage" }),
        new EdgeLabelSpec("WRITES_TO",      FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Database", "Storage" }),
        new EdgeLabelSpec("PUBLISHES",      FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Event", "Queue" }),
        new EdgeLabelSpec("CONSUMES",       FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Event", "Queue" }),
        new EdgeLabelSpec("DEPENDS_ON",     FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Service" }),
        new EdgeLabelSpec("DEFINED_IN",     FromLabels: new[] { "Service", "Endpoint", "Convention", "Capability" },      ToLabels: new[] { "File" }),
        new EdgeLabelSpec("IMPLEMENTS",     FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Capability" }),
        new EdgeLabelSpec("FOLLOWS",        FromLabels: new[] { "Service" },                                              ToLabels: new[] { "Convention" }),
        new EdgeLabelSpec("RELATES_TO",     FromLabels: new[] { "Concept" },                                              ToLabels: new[] { "Concept", "Service", "Capability" }),
        new EdgeLabelSpec("STORES",         FromLabels: new[] { "Storage" },                                              ToLabels: new[] { "Concept" }),
        new EdgeLabelSpec("CONFLICTS_WITH", FromLabels: new[] { "*" },                                                    ToLabels: new[] { "*" }),
    };

    /// <summary>Lookup by exact label name. Returns null if unknown.</summary>
    public static NodeLabelSpec? FindNode(string label) =>
        NodeLabels.FirstOrDefault(n => string.Equals(n.Label, label, StringComparison.Ordinal));

    /// <summary>Lookup by exact label name. Returns null if unknown.</summary>
    public static EdgeLabelSpec? FindEdge(string label) =>
        EdgeLabels.FirstOrDefault(e => string.Equals(e.Label, label, StringComparison.Ordinal));
}

public record NodeLabelSpec(string Label, string[] Required, string[] Optional)
{
    public bool IsKnownProperty(string name) =>
        Required.Contains(name, StringComparer.Ordinal) ||
        Optional.Contains(name, StringComparer.Ordinal);
}

public record EdgeLabelSpec(string Label, string[] FromLabels, string[] ToLabels)
{
    public bool AcceptsFrom(string label) =>
        FromLabels.Contains("*") || FromLabels.Contains(label, StringComparer.Ordinal);

    public bool AcceptsTo(string label) =>
        ToLabels.Contains("*") || ToLabels.Contains(label, StringComparer.Ordinal);
}
