using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// BE-025: manual full re-scan orchestrator. Triggered from
/// <c>POST /api/workspaces/{slug}/repos/{id}/rescan</c>.
///
/// Re-runs Graphify on the current working tree and the semantic extraction
/// pass on every file. The LLM extraction cache is intentionally NOT bypassed:
/// same file content + same prompt version yields the same answer, so the
/// cache is still correct. Users that want a truly fresh extraction (e.g.
/// because the prompt itself was wrong) should bump the prompt version.
///
/// Existing graph nodes are NOT deleted before re-extraction so that queries
/// during a re-scan still return stale-but-usable data. Reconciliation happens
/// via upsert logic in the extraction layer.
///
/// Retry policy: at most one retry, matching <see cref="InitialScanJob"/>.
/// </summary>
[AutomaticRetry(Attempts = 1)]
public sealed class FullRescanJob
{
    private readonly IRepoScanPipeline _pipeline;
    private readonly ArchMindDbContext _db;
    private readonly ILogger<FullRescanJob> _logger;

    public FullRescanJob(
        IRepoScanPipeline pipeline,
        ArchMindDbContext db,
        ILogger<FullRescanJob> logger)
    {
        _pipeline = pipeline;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Clears <c>LastProcessedSha</c> to force the pipeline
    /// to treat the current tree as fully unprocessed, then delegates to the
    /// shared scan pipeline with <c>scanKind = "manual"</c>.
    /// </summary>
    public async Task RunAsync(Guid workspaceId, Guid repoId, CancellationToken ct = default)
    {
        var repo = await _db.Repos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null || repo.WorkspaceId != workspaceId)
        {
            _logger.LogWarning(
                "FullRescanJob skipped: repo not found or workspace mismatch workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        // Clear LastProcessedSha so the pipeline reports FromSha=null on this
        // run and any future diff-scan logic treats the tree as unprocessed.
        // The pipeline immediately overwrites Status to "scanning" itself.
        if (repo.LastProcessedSha is not null)
        {
            repo.LastProcessedSha = null;
            repo.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "FullRescanJob delegating to pipeline workspace={WorkspaceId} repo={RepoId}",
            workspaceId, repoId);

        await _pipeline.RunAsync(workspaceId, repoId, scanKind: "manual", ct);
    }
}
