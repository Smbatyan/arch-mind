namespace ArchMind.Infrastructure.Graph;

/// <summary>
/// Allowlist of valid Apache AGE vertex and edge labels. The graph writer
/// interpolates labels directly into Cypher strings (AGE has no native label
/// parameterisation), so we MUST validate against this set before composing
/// any query — otherwise we have a Cypher injection sink.
/// </summary>
/// <remarks>
/// Source of truth: ArchMind technical spec, Section 6 ("Graph schema"). If
/// <c>docs/graph-schema.md</c> exists in this repo it should match this list;
/// keep them in lockstep. Labels are case-sensitive in AGE.
/// </remarks>
internal static class GraphLabels
{
    public static readonly HashSet<string> Vertex = new(StringComparer.Ordinal)
    {
        "Service",
        "Endpoint",
        "Event",
        "Topic",
        "Storage",
        "Dependency",
        "Convention",
        "Capability",
        "Skill",
        "Team",
    };

    public static readonly HashSet<string> Edge = new(StringComparer.Ordinal)
    {
        "PUBLISHES",
        "CONSUMES",
        "OWNS",
        "READS",
        "EXPOSES",
        "CALLS",
        "PUBLISHED_TO",
        "CONSUMED_FROM",
        "DEPENDS_ON",
        "FOLLOWS",
        "USES_CAPABILITY",
        "APPLIES_TO",
        "OWNED_BY",
    };

    public static bool IsVertex(string label) => Vertex.Contains(label);
    public static bool IsEdge(string label) => Edge.Contains(label);
}
