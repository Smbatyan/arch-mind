namespace ArchMind.Core.Abstractions;

/// <summary>
/// Rebuilds the combined workspace-level graph (graphify-out/combined_graph.html +
/// combined_graph.json) by merging all per-repo graph.json files and colouring
/// nodes by originating repository.
///
/// Called automatically at the end of every successful <see cref="IRepoScanPipeline"/>
/// run so the combined view stays current without requiring a full rescan.
/// </summary>
public interface IWorkspaceGraphService
{
    /// <summary>
    /// Merge all repos in <paramref name="workspaceId"/> that have an existing
    /// <c>graphify-out/graph.json</c> into a combined visualisation. Missing or
    /// not-yet-scanned repos are silently skipped.
    /// </summary>
    Task RebuildAsync(Guid workspaceId, CancellationToken ct = default);
}
