using ArchMind.Core.Abstractions;
using ArchMind.Core.Exceptions;
using ArchMind.Infrastructure.Cloning;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// BE-024: per-repo recurring poll that asks GitHub for the current HEAD SHA
/// on the default branch and, if it differs from <c>LastProcessedSha</c>,
/// enqueues a <see cref="DiffScanJob"/> (or <see cref="InitialScanJob"/> if
/// the repo was somehow never scanned). Cheap by design — no DB writes on
/// the common "no changes" path.
///
/// <para>
/// Retry policy: zero retries. Polling is run on a cron (see
/// <see cref="Polling.PollingOptions"/>), so the next tick is the retry. We
/// don't want Hangfire holding on to a poll attempt that just failed because
/// GitHub rate-limited us — we want the schedule to keep ticking.
/// </para>
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class PollRepoJob
{
    private readonly IGitHubClient _gitHub;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ArchMindDbContext _db;
    private readonly ILogger<PollRepoJob> _logger;

    public PollRepoJob(
        IGitHubClient gitHub,
        IBackgroundJobClient backgroundJobClient,
        ArchMindDbContext db,
        ILogger<PollRepoJob> logger)
    {
        _gitHub = gitHub;
        _backgroundJobClient = backgroundJobClient;
        _db = db;
        _logger = logger;
    }

    public async Task RunAsync(Guid workspaceId, Guid repoId, CancellationToken ct = default)
    {
        var repo = await _db.Repos.FirstOrDefaultAsync(
            r => r.Id == repoId && r.WorkspaceId == workspaceId, ct);
        if (repo is null)
        {
            _logger.LogDebug(
                "PollRepoJob skipped: repo not found workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        // Already running an end-to-end scan — let it finish.
        if (string.Equals(repo.Status, "scanning", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "PollRepoJob skipped: repo is scanning workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        string owner;
        string name;
        try
        {
            (owner, name) = GitHubUrlParser.Parse(repo.GitHubUrl);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                ex,
                "PollRepoJob skipped: unparseable github url workspace={WorkspaceId} repo={RepoId} url={Url}",
                workspaceId, repoId, repo.GitHubUrl);
            return;
        }

        string latestSha;
        try
        {
            latestSha = await _gitHub.GetLatestCommitShaAsync(
                owner, name, repo.DefaultBranch, repo.PatToken, ct);
        }
        catch (GitHubRateLimitException ex)
        {
            _logger.LogWarning(
                "PollRepoJob rate-limited workspace={WorkspaceId} repo={RepoId} resetAt={ResetAt} message={Message}",
                workspaceId, repoId, ex.ResetAt, ex.Message);
            return;
        }
        catch (GitHubAuthException ex)
        {
            _logger.LogWarning(
                ex,
                "PollRepoJob auth failure workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);

            repo.Status = "failed";
            repo.ErrorMessage = "GitHub auth failed during polling; verify PAT";
            repo.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(
                    saveEx,
                    "PollRepoJob failed to persist auth-failure status workspace={WorkspaceId} repo={RepoId}",
                    workspaceId, repoId);
            }
            return;
        }
        catch (GitHubNotFoundException ex)
        {
            _logger.LogWarning(
                ex,
                "PollRepoJob not-found workspace={WorkspaceId} repo={RepoId} owner={Owner} name={Name} branch={Branch}",
                workspaceId, repoId, owner, name, repo.DefaultBranch);
            return;
        }
        catch (GitHubClientException ex)
        {
            _logger.LogWarning(
                ex,
                "PollRepoJob github client failure workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected — let Hangfire record it (AutomaticRetry=0 means no
            // retry storm; the next cron tick is the recovery path).
            _logger.LogError(
                ex,
                "PollRepoJob unexpected error workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            throw;
        }

        if (string.Equals(latestSha, repo.LastProcessedSha, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "PollRepoJob no changes workspace={WorkspaceId} repo={RepoId} sha={Sha}",
                workspaceId, repoId, latestSha);
            return;
        }

        if (string.IsNullOrEmpty(repo.LastProcessedSha))
        {
            // Recover gracefully: repo was registered but never got an
            // initial scan completion. Re-trigger the initial pipeline rather
            // than starting from a phantom diff base.
            _logger.LogWarning(
                "PollRepoJob found never-scanned repo; enqueuing InitialScanJob workspace={WorkspaceId} repo={RepoId} latest={Latest}",
                workspaceId, repoId, latestSha);
            _backgroundJobClient.Enqueue<InitialScanJob>(
                j => j.RunAsync(workspaceId, repoId, default));
            return;
        }

        _logger.LogInformation(
            "PollRepoJob detected new commits workspace={WorkspaceId} repo={RepoId} from={From} to={To}",
            workspaceId, repoId, repo.LastProcessedSha, latestSha);

        var fromSha = repo.LastProcessedSha;
        _backgroundJobClient.Enqueue<DiffScanJob>(
            j => j.RunAsync(workspaceId, repoId, fromSha, latestSha, default));
    }
}
