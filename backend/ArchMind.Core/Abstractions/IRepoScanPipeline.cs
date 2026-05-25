namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-025: shared end-to-end scan pipeline used by both <c>InitialScanJob</c>
/// and <c>FullRescanJob</c>. Both job classes are thin Hangfire shells that
/// delegate to this pipeline; only the <paramref name="scanKind"/> recorded in
/// <c>scan_runs</c> differs between callers.
///
/// Implementations are expected to:
/// <list type="number">
///   <item>Mark the repo as <c>scanning</c> and insert a <c>scan_runs</c> row.</item>
///   <item>Clone (or refresh) the working tree.</item>
///   <item>Run Graphify against the working tree.</item>
///   <item>Enqueue per-file LLM extraction jobs (cache-aware).</item>
///   <item>Update repo + scan_run rows on success / failure.</item>
/// </list>
/// </summary>
public interface IRepoScanPipeline
{
    /// <param name="scanKind">
    /// Persisted as <c>scan_runs.kind</c>. Accepts <c>"initial"</c> or <c>"manual"</c>.
    /// </param>
    Task RunAsync(Guid workspaceId, Guid repoId, string scanKind, CancellationToken ct);
}
