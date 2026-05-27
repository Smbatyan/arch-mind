namespace ArchMind.Core.Abstractions;

/// <summary>
/// Reads the workspace-level combined structural graph
/// (<c>{workspaceDir}/graphify-out/combined_graph.json</c>) produced by
/// <see cref="IWorkspaceGraphService"/>. The structural graph is the raw AST
/// extracted by graphify — no LLM enrichment. Used by the admin UI's
/// "Structural" tab and by NL-search.
/// </summary>
public interface IStructuralGraphService
{
    /// <summary>
    /// Load (or return cached) structural data for a workspace, optionally
    /// filtered to a single repo and capped at <paramref name="limit"/>
    /// highest-degree nodes.
    /// </summary>
    Task<StructuralGraphData> GetAsync(
        Guid workspaceId,
        Guid? repoId,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Return node IDs whose label/name contains any of the supplied
    /// case-insensitive keywords. Backs LLM-assisted search: the LLM
    /// converts a natural-language query into a keyword list, then this
    /// method does the lookup deterministically (no token cost per query
    /// beyond the keyword-extraction step).
    /// </summary>
    Task<IReadOnlyList<string>> SearchByKeywordsAsync(
        Guid workspaceId,
        IReadOnlyList<string> keywords,
        int limit,
        CancellationToken ct = default);
}

public sealed record StructuralGraphNode(
    string Id,
    string Label,
    string Name,
    string? RepoId,
    string? SourceFile,
    int Degree);

public sealed record StructuralGraphEdge(
    string Id,
    string Source,
    string Target,
    string Label);

public sealed record StructuralGraphData(
    IReadOnlyList<StructuralGraphNode> Nodes,
    IReadOnlyList<StructuralGraphEdge> Edges,
    int TotalNodes,
    int TotalEdges,
    bool Truncated);
