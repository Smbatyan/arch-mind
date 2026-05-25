using System.Security.Cryptography;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Core.Exceptions;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// BE-019: end-to-end initial-scan orchestrator. Triggered once per repo from the
/// repo-creation endpoint. Clones the repo, runs Graphify, enqueues one
/// <see cref="LlmExtractionJob"/> per scannable file, and records a row in
/// <c>scan_runs</c> for telemetry.
///
/// Per-file extraction jobs are fire-and-forget under Hangfire — this orchestrator
/// does NOT await their completion. Cross-file correlation (BE-026) will await
/// the batch in Sprint 3.
///
/// Retry policy: at most one retry. A 30-minute pipeline that has failed twice is
/// better surfaced as a failed status than silently re-run.
/// </summary>
[AutomaticRetry(Attempts = 1)]
public sealed class InitialScanJob
{
    // Directory names anywhere in the relative path that disqualify a file.
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".github",
        ".next",
        ".nuxt",
        ".venv",
        ".idea",
        ".vscode",
        ".cache",
        ".turbo",
        ".gradle",
        ".mvn",
        ".terraform",
        "node_modules",
        "bower_components",
        "vendor",
        "dist",
        "build",
        "out",
        "target",
        "bin",
        "obj",
        "__pycache__",
        "coverage",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg",
        ".pdf",
        ".zip", ".tar", ".gz", ".tgz", ".bz2", ".7z", ".rar",
        ".ttf", ".woff", ".woff2", ".otf", ".eot",
        ".so", ".dll", ".exe", ".dylib", ".a", ".lib", ".o",
        ".class", ".jar", ".pyc", ".wasm",
        ".mp3", ".mp4", ".mov", ".avi", ".wav", ".flac",
        ".db", ".sqlite", ".sqlite3",
    };

    private const long MaxFileSizeBytes = 1024 * 1024; // 1 MiB

    private readonly IRepoCloneService _cloneService;
    private readonly IGraphifyRunner _graphifyRunner;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ArchMindDbContext _db;
    private readonly ILogger<InitialScanJob> _logger;

    public InitialScanJob(
        IRepoCloneService cloneService,
        IGraphifyRunner graphifyRunner,
        IBackgroundJobClient backgroundJobClient,
        ArchMindDbContext db,
        ILogger<InitialScanJob> logger)
    {
        _cloneService = cloneService;
        _graphifyRunner = graphifyRunner;
        _backgroundJobClient = backgroundJobClient;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Workspace id is passed explicitly so the orchestrator
    /// can guard against cross-workspace mismatches even if the repo id is stale.
    /// </summary>
    public async Task RunAsync(Guid workspaceId, Guid repoId, CancellationToken ct = default)
    {
        var repo = await _db.Repos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null || repo.WorkspaceId != workspaceId)
        {
            _logger.LogWarning(
                "InitialScanJob skipped: repo not found or workspace mismatch workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        // Mark repo scanning + create the scan_runs row in a single save.
        var now = DateTime.UtcNow;
        repo.Status = "scanning";
        repo.ErrorMessage = null;
        repo.UpdatedAt = now;

        var scanRun = new ScanRun
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RepoId = repoId,
            Kind = "initial",
            Status = "running",
            StartedAt = now,
            FromSha = repo.LastProcessedSha,
        };
        _db.ScanRuns.Add(scanRun);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "InitialScanJob starting workspace={WorkspaceId} repo={RepoId} scanRun={ScanRunId}",
            workspaceId, repoId, scanRun.Id);

        try
        {
            // 1. Clone.
            await _cloneService.CloneAsync(repo, ct);
            var currentSha = await _cloneService.GetCurrentShaAsync(repo, ct);
            scanRun.ToSha = currentSha;

            // 2. Graphify. Output is captured to scan_runs for debug; full graph
            // integration with AGE happens in Sprint 3.
            var graphifyOutput = await _graphifyRunner.RunAsync(repo.WorkingDirPath, ct);
            scanRun.GraphifyNodes = graphifyOutput.Nodes.Count;
            scanRun.GraphifyEdges = graphifyOutput.Edges.Count;

            _logger.LogInformation(
                "Graphify complete workspace={WorkspaceId} repo={RepoId} nodes={Nodes} edges={Edges}",
                workspaceId, repoId, scanRun.GraphifyNodes, scanRun.GraphifyEdges);

            // 3. Enumerate working tree + enqueue per-file extraction jobs.
            var (scanned, enqueued) = EnumerateAndEnqueue(workspaceId, repoId, repo.WorkingDirPath, ct);
            scanRun.FilesScanned = scanned;
            scanRun.FilesEnqueued = enqueued;

            _logger.LogInformation(
                "Per-file extraction jobs enqueued workspace={WorkspaceId} repo={RepoId} scanned={Scanned} enqueued={Enqueued}",
                workspaceId, repoId, scanned, enqueued);

            // 4. Mark repo + scan_run as succeeded.
            var completedAt = DateTime.UtcNow;
            repo.LastProcessedSha = currentSha;
            repo.Status = "scanned";
            repo.UpdatedAt = completedAt;

            scanRun.Status = "succeeded";
            scanRun.CompletedAt = completedAt;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "InitialScanJob complete workspace={WorkspaceId} repo={RepoId} sha={Sha}",
                workspaceId, repoId, currentSha);

            // TODO: After Sprint 3, wait for all per-file jobs to complete via
            // Hangfire BatchJob.AwaitAll before running cross-file correlation.
            // TODO: After Sprint 5, run clarification engine.
        }
        catch (Exception ex) when (
            ex is RepoAuthException
                or RepoNetworkException
                or RepoCloneException
                or GraphifyExecutionException
                or GraphifyTimeoutException
                or GraphifyOutputMalformedException)
        {
            await RecordFailureAsync(repo, scanRun, ex, ct);
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(repo, scanRun, ex, ct);
            throw;
        }
    }

    private async Task RecordFailureAsync(Repo repo, ScanRun scanRun, Exception ex, CancellationToken ct)
    {
        var message = BuildErrorMessage(ex);
        var now = DateTime.UtcNow;

        repo.Status = "failed";
        repo.ErrorMessage = Truncate(message, 2000);
        repo.UpdatedAt = now;

        scanRun.Status = "failed";
        scanRun.CompletedAt = now;
        scanRun.ErrorMessage = Truncate(message, 4000);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception saveEx)
        {
            // We're already in a failure path — don't mask the original exception.
            _logger.LogError(
                saveEx,
                "Failed to persist InitialScanJob failure state workspace={WorkspaceId} repo={RepoId}",
                repo.WorkspaceId, repo.Id);
        }

        _logger.LogError(
            ex,
            "InitialScanJob failed workspace={WorkspaceId} repo={RepoId} message={Message}",
            repo.WorkspaceId, repo.Id, message);
    }

    private static string BuildErrorMessage(Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex.Message);

        // Surface captured stderr where available (clone + graphify exceptions).
        var stderr = ex switch
        {
            RepoCloneException rce => rce.Stderr,
            GraphifyExecutionException gee => gee.Stderr,
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.Append(" | stderr: ");
            sb.Append(Truncate(stderr.Trim(), 1500));
        }

        return sb.ToString();
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value.Substring(0, max);
    }

    /// <summary>
    /// Walks the cloned working tree and enqueues a per-file extraction job for
    /// each file that passes the ignore filters. Returns
    /// (totalFilesConsidered, filesEnqueued).
    /// </summary>
    private (int Scanned, int Enqueued) EnumerateAndEnqueue(
        Guid workspaceId,
        Guid repoId,
        string workingDirPath,
        CancellationToken ct)
    {
        if (!Directory.Exists(workingDirPath))
        {
            _logger.LogWarning(
                "Working dir does not exist after clone workspace={WorkspaceId} repo={RepoId} path={Path}",
                workspaceId, repoId, workingDirPath);
            return (0, 0);
        }

        var root = Path.GetFullPath(workingDirPath);
        int scanned = 0;
        int enqueued = 0;

        foreach (var absolutePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            var relativePath = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

            if (ShouldIgnore(relativePath, absolutePath))
            {
                continue;
            }

            string content;
            try
            {
                var bytes = File.ReadAllBytes(absolutePath);
                // Reject if file looks binary (NUL byte in first 8 KiB).
                if (LooksBinary(bytes))
                {
                    continue;
                }
                content = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping file (read/decode failure) workspace={WorkspaceId} repo={RepoId} file={FilePath}",
                    workspaceId, repoId, relativePath);
                continue;
            }

            var contentHash = ComputeSha256Hex(content);

            _backgroundJobClient.Enqueue<LlmExtractionJob>(
                j => j.RunAsync(workspaceId, repoId, relativePath, contentHash, default));
            enqueued++;
        }

        return (scanned, enqueued);
    }

    private static bool ShouldIgnore(string relativePath, string absolutePath)
    {
        // Reject any path segment that matches our ignore list.
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IgnoredDirectories.Contains(segment))
            {
                return true;
            }
            // Belt-and-suspenders: any dot-directory we didn't explicitly list.
            if (segment.Length > 1 && segment[0] == '.' && !segment.Equals(".env", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var extension = Path.GetExtension(relativePath);
        if (BinaryExtensions.Contains(extension))
        {
            return true;
        }

        try
        {
            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                return true;
            }
        }
        catch
        {
            // Unable to stat the file — treat as ignored rather than crash the walk.
            return true;
        }

        return false;
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

    private static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
