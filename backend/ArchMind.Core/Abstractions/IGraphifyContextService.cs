namespace ArchMind.Core.Abstractions;

/// <summary>
/// Provides structural (AST-level) context extracted by Graphify for a specific
/// file within a cloned repository. Used by <c>LlmExtractionJob</c> to enrich
/// LLM prompts with pre-computed structural relationships so the model does not
/// have to infer import graphs, call graphs, or class hierarchies from raw text.
/// </summary>
public interface IGraphifyContextService
{
    /// <summary>
    /// Returns Graphify-extracted structural nodes and edges whose
    /// <c>file_path</c> matches <paramref name="filePath"/>.
    /// Returns <see cref="GraphifyFileContext.Empty"/> when the Graphify output
    /// file does not exist (e.g., Graphify has not run yet) or cannot be parsed.
    /// Never throws — failures are logged and treated as missing context.
    /// </summary>
    Task<GraphifyFileContext> GetFileContextAsync(
        Guid workspaceId,
        Guid repoId,
        string filePath,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the full Graphify output (all nodes + all edges) for the given
    /// repo, used by workspace-wide correlation jobs that need cross-file
    /// IMPORTS/CALLS edges to stitch service-level DEPENDS_ON relationships.
    /// Returns <c>null</c> when the graphify output file is missing or unparseable.
    /// Never throws.
    /// </summary>
    Task<GraphifyOutput?> GetRepoGraphAsync(
        Guid workspaceId,
        Guid repoId,
        CancellationToken ct = default);
}

/// <summary>
/// Structural context for a single file as extracted by Graphify.
/// </summary>
public sealed record GraphifyFileContext(
    IReadOnlyList<GraphifyNode> Nodes,
    IReadOnlyList<GraphifyEdge> OutboundEdges,
    IReadOnlyList<GraphifyEdge> InboundEdges)
{
    public static readonly GraphifyFileContext Empty = new(
        Array.Empty<GraphifyNode>(),
        Array.Empty<GraphifyEdge>(),
        Array.Empty<GraphifyEdge>());

    public bool IsEmpty => Nodes.Count == 0;
}
