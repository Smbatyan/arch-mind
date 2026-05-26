using ArchMind.Core.Models.Graph;

namespace ArchMind.Infrastructure.Graph;

/// <summary>
/// Allowlist of valid Apache AGE vertex and edge labels. The graph writer
/// interpolates labels directly into Cypher strings (AGE has no native label
/// parameterisation), so we MUST validate against this set before composing
/// any query — otherwise we have a Cypher injection sink.
/// </summary>
/// <remarks>
/// BE-044: this set is now <i>derived</i> from
/// <see cref="GraphSchema"/> so the canonical schema and the
/// Cypher-injection allowlist cannot drift. Add a new label by editing
/// <see cref="GraphSchema.NodeLabels"/> / <see cref="GraphSchema.EdgeLabels"/>;
/// this set picks it up automatically. Labels are case-sensitive in AGE.
/// </remarks>
internal static class GraphLabels
{
    public static readonly HashSet<string> Vertex = new(
        GraphSchema.NodeLabels.Select(n => n.Label),
        StringComparer.Ordinal);

    public static readonly HashSet<string> Edge = new(
        GraphSchema.EdgeLabels.Select(e => e.Label),
        StringComparer.Ordinal);

    public static bool IsVertex(string label) => Vertex.Contains(label);
    public static bool IsEdge(string label) => Edge.Contains(label);
}
