using ArchMind.Core.Abstractions;
using Hangfire;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// BE-019: end-to-end initial-scan orchestrator. Triggered once per repo from
/// the repo-creation endpoint.
///
/// BE-025 refactor: the pipeline body has been extracted into
/// <see cref="IRepoScanPipeline"/> so the same flow can be reused by
/// <see cref="FullRescanJob"/>. This class is now a thin Hangfire shell that
/// delegates to the pipeline with <c>scanKind = "initial"</c>.
///
/// Retry policy: at most one retry. A 30-minute pipeline that has failed twice
/// is better surfaced as a failed status than silently re-run.
/// </summary>
[AutomaticRetry(Attempts = 1)]
[Queue("scan")]
public sealed class InitialScanJob
{
    private readonly IRepoScanPipeline _pipeline;

    public InitialScanJob(IRepoScanPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    /// <summary>
    /// Hangfire entry point. Workspace id is passed explicitly so the
    /// orchestrator can guard against cross-workspace mismatches even if the
    /// repo id is stale.
    ///
    /// Routed to the "scan" queue so a dedicated Hangfire server (WorkerCount=5)
    /// caps simultaneous repo scans at 5 while leaving the default-queue worker
    /// pool free to process per-file LlmExtractionJob items at its own pace.
    /// </summary>
    public Task RunAsync(Guid workspaceId, Guid repoId, CancellationToken ct = default)
        => _pipeline.RunAsync(workspaceId, repoId, scanKind: "initial", ct);
}
