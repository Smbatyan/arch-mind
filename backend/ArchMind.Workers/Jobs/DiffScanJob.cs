using System.Security.Cryptography;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Core.Exceptions;
using ArchMind.Infrastructure.Cloning;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// BE-023: per-commit diff scanner. Triggered after polling (BE-024) detects a
/// new HEAD on a tracked branch. Compares <c>fromSha..toSha</c> via the GitHub
/// compare API, brings the local working tree up-to-date, and dispatches per-file
/// extraction or orphan-removal work depending on the change kind.
///
/// Unlike <see cref="InitialScanJob"/> this job does NOT re-run Graphify — diff
/// scans are per-file and we treat the graph layer as the source of truth for
/// existing nodes. Stale nodes are pruned via
/// <see cref="IGraphWriter.RemoveOrphansForFileAsync"/> as files change.
///
/// Retry policy: 2 retries with 60s and 300s backoff, primarily to absorb
/// transient GitHub or network failures. Auth and not-found errors short-circuit
/// to a "failed" status without retry.
/// </summary>
[AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 60, 300 })]
public sealed class DiffScanJob
{
    private const long MaxFileSizeBytes = 1024 * 1024; // 1 MiB
    private const int MaxFilesPerDiff = 500;

    private readonly IGitHubClient _gitHubClient;
    private readonly IRepoCloneService _cloneService;
    private readonly IGraphWriter _graphWriter;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ArchMindDbContext _db;
    private readonly ILogger<DiffScanJob> _logger;

    public DiffScanJob(
        IGitHubClient gitHubClient,
        IRepoCloneService cloneService,
        IGraphWriter graphWriter,
        IBackgroundJobClient backgroundJobClient,
        ArchMindDbContext db,
        ILogger<DiffScanJob> logger)
    {
        _gitHubClient = gitHubClient;
        _cloneService = cloneService;
        _graphWriter = graphWriter;
        _backgroundJobClient = backgroundJobClient;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. <paramref name="fromSha"/> is the
    /// <c>repo.last_processed_sha</c> as observed by the poller; <paramref name="toSha"/>
    /// is the new HEAD of the tracked branch.
    /// </summary>
    public async Task RunAsync(
        Guid workspaceId,
        Guid repoId,
        string fromSha,
        string toSha,
        CancellationToken ct = default)
    {
        var repo = await _db.Repos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null || repo.WorkspaceId != workspaceId)
        {
            _logger.LogWarning(
                "DiffScanJob skipped: repo not found or workspace mismatch workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        var now = DateTime.UtcNow;
        repo.Status = "scanning";
        repo.ErrorMessage = null;
        repo.UpdatedAt = now;

        var scanRun = new ScanRun
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RepoId = repoId,
            Kind = "diff",
            Status = "running",
            StartedAt = now,
            FromSha = fromSha,
            ToSha = toSha,
        };
        _db.ScanRuns.Add(scanRun);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "DiffScanJob starting workspace={WorkspaceId} repo={RepoId} from={FromSha} to={ToSha} scanRun={ScanRunId}",
            workspaceId, repoId, fromSha, toSha, scanRun.Id);

        try
        {
            // 1. Resolve GitHub owner/name from the configured repo URL.
            var (owner, name) = GitHubUrlParser.Parse(repo.GitHubUrl);

            // 2. Diff via GitHub API.
            var compare = await _gitHubClient.CompareCommitsAsync(
                owner, name, fromSha, toSha, repo.PatToken, ct);

            var files = compare.Files;
            // TODO: Handle huge diffs via batched child jobs. For MVP we cap at
            // MaxFilesPerDiff and log a warning recommending a full re-scan.
            if (files.Count > MaxFilesPerDiff)
            {
                _logger.LogWarning(
                    "Diff too large; full re-scan recommended workspace={WorkspaceId} repo={RepoId} fileCount={Count} cap={Cap}",
                    workspaceId, repoId, files.Count, MaxFilesPerDiff);
                files = files.Take(MaxFilesPerDiff).ToList();
            }

            // 3. Bring working tree up-to-date with origin/<DefaultBranch>.
            // This should advance HEAD to toSha (or close to it) for content reads.
            await _cloneService.UpdateAsync(repo, ct);

            // 4. Per-file dispatch.
            var totalFiles = files.Count;
            int enqueued = 0;
            int removed = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                switch (file.Status)
                {
                    case "removed":
                        await _graphWriter.RemoveOrphansForFileAsync(workspaceId, repoId, file.Filename, ct);
                        removed++;
                        break;

                    case "renamed":
                        // Old-path nodes are orphaned (file_path no longer matches),
                        // new path is treated as an add.
                        if (!string.IsNullOrWhiteSpace(file.PreviousFilename))
                        {
                            await _graphWriter.RemoveOrphansForFileAsync(workspaceId, repoId, file.PreviousFilename!, ct);
                            removed++;
                        }
                        if (TryEnqueueExtraction(workspaceId, repoId, repo.WorkingDirPath, file.Filename))
                        {
                            enqueued++;
                        }
                        break;

                    case "added":
                    case "modified":
                        if (TryEnqueueExtraction(workspaceId, repoId, repo.WorkingDirPath, file.Filename))
                        {
                            enqueued++;
                        }
                        break;

                    default:
                        _logger.LogDebug(
                            "Skipping file with unknown status workspace={WorkspaceId} repo={RepoId} file={FilePath} status={Status}",
                            workspaceId, repoId, file.Filename, file.Status);
                        break;
                }
            }

            // 5. Mark repo + scan_run succeeded.
            var completedAt = DateTime.UtcNow;
            repo.LastProcessedSha = toSha;
            repo.Status = "scanned";
            repo.UpdatedAt = completedAt;

            scanRun.Status = "succeeded";
            scanRun.CompletedAt = completedAt;
            scanRun.FilesScanned = totalFiles;
            scanRun.FilesEnqueued = enqueued;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "DiffScanJob complete workspace={WorkspaceId} repo={RepoId} scanned={Scanned} enqueued={Enqueued} removed={Removed}",
                workspaceId, repoId, totalFiles, enqueued, removed);

            // TODO: After cross-file correlation (BE-026), enqueue
            // CrossFileCorrelationJob here so service-to-service edges get
            // recomputed after per-file extraction settles.
        }
        catch (GitHubAuthException ex)
        {
            await RecordFailureAsync(repo, scanRun, ex,
                "GitHub authentication failed; verify PAT", ct);
            // Don't retry on auth failure — re-throwing wastes Hangfire slots and
            // produces no different outcome until the PAT is rotated.
            throw;
        }
        catch (GitHubNotFoundException ex)
        {
            await RecordFailureAsync(repo, scanRun, ex,
                "Repo or commit not found on GitHub.", ct);
            throw;
        }
        catch (GitHubRateLimitException ex)
        {
            await RecordFailureAsync(repo, scanRun, ex,
                "GitHub rate limit exceeded; retry later.", ct);
            // Re-throw so Hangfire applies its backoff and retries.
            throw;
        }
        catch (RepoNetworkException ex)
        {
            await RecordFailureAsync(repo, scanRun, ex,
                "Repo network failure; retrying.", ct);
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(repo, scanRun, ex, ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// Reads the local working-tree file, validates size + binary heuristics,
    /// and enqueues an <see cref="LlmExtractionJob"/> for it. Returns true when
    /// a job was enqueued.
    /// </summary>
    private bool TryEnqueueExtraction(Guid workspaceId, Guid repoId, string workingDirPath, string relativePath)
    {
        var absolutePath = Path.Combine(workingDirPath, relativePath);

        if (!File.Exists(absolutePath))
        {
            _logger.LogDebug(
                "Skipping extraction: file missing from working tree workspace={WorkspaceId} repo={RepoId} file={FilePath}",
                workspaceId, repoId, relativePath);
            return false;
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(absolutePath);
            if (info.Length > MaxFileSizeBytes)
            {
                _logger.LogDebug(
                    "Skipping extraction: file too large workspace={WorkspaceId} repo={RepoId} file={FilePath} size={Size}",
                    workspaceId, repoId, relativePath, info.Length);
                return false;
            }

            bytes = File.ReadAllBytes(absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Skipping extraction: read failure workspace={WorkspaceId} repo={RepoId} file={FilePath}",
                workspaceId, repoId, relativePath);
            return false;
        }

        if (LooksBinary(bytes))
        {
            _logger.LogDebug(
                "Skipping extraction: binary content workspace={WorkspaceId} repo={RepoId} file={FilePath}",
                workspaceId, repoId, relativePath);
            return false;
        }

        var contentHash = ComputeSha256Hex(bytes);

        _backgroundJobClient.Enqueue<LlmExtractionJob>(
            j => j.RunAsync(workspaceId, repoId, relativePath, contentHash, default));
        return true;
    }

    private async Task RecordFailureAsync(Repo repo, ScanRun scanRun, Exception ex, string userMessage, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        repo.Status = "failed";
        repo.ErrorMessage = Truncate(userMessage, 2000);
        repo.UpdatedAt = now;

        scanRun.Status = "failed";
        scanRun.CompletedAt = now;
        scanRun.ErrorMessage = Truncate(userMessage, 4000);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception saveEx)
        {
            _logger.LogError(
                saveEx,
                "Failed to persist DiffScanJob failure state workspace={WorkspaceId} repo={RepoId}",
                repo.WorkspaceId, repo.Id);
        }

        _logger.LogError(
            ex,
            "DiffScanJob failed workspace={WorkspaceId} repo={RepoId} message={Message}",
            repo.WorkspaceId, repo.Id, userMessage);
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, max);
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var probeLen = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < probeLen; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
