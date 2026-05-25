namespace ArchMind.Core.Abstractions;

/// <summary>
/// Wraps the Graphify CLI as a subprocess. Given a cloned repository on local disk,
/// produces a structural AST graph (nodes + edges) which ArchMind then layers semantic
/// LLM-extracted concepts on top of.
/// </summary>
public interface IGraphifyRunner
{
    /// <summary>
    /// Runs Graphify against <paramref name="repoPath"/> and returns the parsed graph.
    /// </summary>
    /// <param name="repoPath">Absolute path to a cloned repository. Graphify will write its
    /// output under <c>{repoPath}/graphify-out/</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GraphifyOutput> RunAsync(string repoPath, CancellationToken ct = default);
}

public record GraphifyOutput(
    IReadOnlyList<GraphifyNode> Nodes,
    IReadOnlyList<GraphifyEdge> Edges,
    GraphifyMetadata Metadata
);

public record GraphifyNode(
    string Id,
    string Type,
    string? Name,
    string? FilePath,
    IReadOnlyDictionary<string, object?> Properties
);

public record GraphifyEdge(
    string Source,
    string Target,
    string Type,
    IReadOnlyDictionary<string, object?> Properties
);

public record GraphifyMetadata(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    int TotalFiles,
    int TotalNodes,
    int TotalEdges,
    IReadOnlyDictionary<string, object?>? Extras
);
