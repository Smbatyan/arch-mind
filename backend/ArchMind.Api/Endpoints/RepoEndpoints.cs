using System.Security.Claims;
using System.Text.RegularExpressions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using ArchMind.Workers.Jobs;
using ArchMind.Workers.Polling;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for repo CRUD inside a workspace. Routes mounted under
/// /api/workspaces/{slug}/repos so the slug -> workspace lookup happens once per
/// request. All routes require an authenticated session and verify workspace
/// membership.
/// </summary>
public static partial class RepoEndpoints
{
    // Must be: https://github.com/{owner}/{repo}[.git]
    [GeneratedRegex(@"^https://github\.com/[^/]+/[^/]+(?:\.git)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubUrlRegex();

    public static IEndpointRouteBuilder MapRepoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/repos");

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapGet("/{id:guid}/scans", ListScansAsync);
        group.MapPost("/{id:guid}/rescan", RescanAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record CreateRepoRequest(string? GitHubUrl, string? DefaultBranch, string? PatToken);

    public sealed record CreateRepoResponse(
        Guid Id,
        string GitHubUrl,
        string DefaultBranch,
        string Status,
        DateTime CreatedAt);

    public sealed record RepoResponse(
        Guid Id,
        string GitHubUrl,
        string DefaultBranch,
        string? LastProcessedSha,
        string Status,
        string? ErrorMessage,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    public sealed record ScanRunResponse(
        Guid Id,
        string Kind,
        string Status,
        DateTime StartedAt,
        DateTime? CompletedAt,
        string? FromSha,
        string? ToSha,
        int FilesScanned,
        int FilesEnqueued,
        int GraphifyNodes,
        int GraphifyEdges,
        long TotalTokens,
        decimal TotalCostUsd,
        string? ErrorMessage);

    // ---------------------------------------------------------------------
    // Handlers
    // ---------------------------------------------------------------------
    private static async Task<IResult> CreateAsync(
        string slug,
        [FromBody] CreateRepoRequest req,
        ArchMindDbContext db,
        IBackgroundJobClient backgroundJobClient,
        IPollingRegistrar pollingRegistrar,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        var githubUrl = req.GitHubUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(githubUrl) || githubUrl.Length > 500 || !GitHubUrlRegex().IsMatch(githubUrl))
        {
            return Results.BadRequest(new { error = "invalid github url" });
        }

        var defaultBranch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch.Trim();
        if (defaultBranch.Length > 200)
        {
            return Results.BadRequest(new { error = "invalid default branch" });
        }

        var patToken = req.PatToken ?? string.Empty;
        if (string.IsNullOrWhiteSpace(patToken) || patToken.Length > 500)
        {
            return Results.BadRequest(new { error = "invalid pat token" });
        }

        // Generate id client-side so we can compute working_dir_path before insert.
        var repoId = Guid.NewGuid();
        var workingDirPath = $"/var/archmind/workspaces/{workspace.Id}/repos/{repoId}/";

        var repo = new Repo
        {
            Id = repoId,
            WorkspaceId = workspace.Id,
            GitHubUrl = githubUrl,
            DefaultBranch = defaultBranch,
            PatToken = patToken,
            WorkingDirPath = workingDirPath,
            Status = "pending",
        };

        db.Repos.Add(repo);
        await db.SaveChangesAsync(ct);

        // BE-019: kick off the initial scan orchestrator. Fire-and-forget — the
        // orchestrator itself moves the repo through scanning → scanned/failed.
        backgroundJobClient.Enqueue<InitialScanJob>(
            j => j.RunAsync(workspace.Id, repo.Id, default));

        // BE-024: register the recurring poll for this repo. No-op when
        // Polling:Enabled = false.
        pollingRegistrar.RegisterRepo(workspace.Id, repo.Id);

        var response = new CreateRepoResponse(
            repo.Id,
            repo.GitHubUrl,
            repo.DefaultBranch,
            repo.Status,
            repo.CreatedAt);

        return Results.Created($"/api/workspaces/{slug}/repos/{repo.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        string slug,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        var repos = await db.Repos
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspace.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new RepoResponse(
                r.Id,
                r.GitHubUrl,
                r.DefaultBranch,
                r.LastProcessedSha,
                r.Status,
                r.ErrorMessage,
                r.CreatedAt,
                r.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(repos);
    }

    private static async Task<IResult> GetByIdAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        var repo = await db.Repos
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspace.Id && r.Id == id)
            .Select(r => new RepoResponse(
                r.Id,
                r.GitHubUrl,
                r.DefaultBranch,
                r.LastProcessedSha,
                r.Status,
                r.ErrorMessage,
                r.CreatedAt,
                r.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        return repo is null ? NotFound() : Results.Ok(repo);
    }

    private static async Task<IResult> ListScansAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        HttpContext httpContext,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        // Confirm the repo exists in this workspace; collapse 404 otherwise so we
        // don't leak existence of foreign repos.
        var repoExists = await db.Repos
            .AsNoTracking()
            .AnyAsync(r => r.WorkspaceId == workspace.Id && r.Id == id, ct);
        if (!repoExists)
        {
            return NotFound();
        }

        var take = Math.Clamp(limit ?? 10, 1, 100);

        var scans = await db.ScanRuns
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspace.Id && s.RepoId == id)
            .OrderByDescending(s => s.StartedAt)
            .Take(take)
            .Select(s => new ScanRunResponse(
                s.Id,
                s.Kind,
                s.Status,
                s.StartedAt,
                s.CompletedAt,
                s.FromSha,
                s.ToSha,
                s.FilesScanned,
                s.FilesEnqueued,
                s.GraphifyNodes,
                s.GraphifyEdges,
                s.TotalTokens,
                s.TotalCostUsd,
                s.ErrorMessage))
            .ToListAsync(ct);

        return Results.Ok(scans);
    }

    /// <summary>
    /// BE-025: enqueue a manual full re-scan of an existing repo. Fire-and-forget
    /// — the job moves the repo through scanning → scanned/failed itself. The
    /// LLM extraction cache is reused: same file content + same prompt version
    /// returns the same answer. Bump the prompt version for a truly fresh run.
    /// </summary>
    private static async Task<IResult> RescanAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        IBackgroundJobClient backgroundJobClient,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        var repo = await db.Repos
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspace.Id && r.Id == id)
            .Select(r => new { r.Id, r.Status })
            .FirstOrDefaultAsync(ct);

        if (repo is null)
        {
            return NotFound();
        }

        if (string.Equals(repo.Status, "scanning", StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "already scanning" },
                statusCode: StatusCodes.Status409Conflict);
        }

        backgroundJobClient.Enqueue<FullRescanJob>(
            j => j.RunAsync(workspace.Id, repo.Id, default));

        return Results.Json(
            new { message = "rescan queued" },
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> DeleteAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        IPollingRegistrar pollingRegistrar,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        var deleted = await db.Repos
            .Where(r => r.WorkspaceId == workspace.Id && r.Id == id)
            .ExecuteDeleteAsync(ct);

        if (deleted == 0)
        {
            return NotFound();
        }

        // BE-024: remove the recurring poll job for this repo.
        pollingRegistrar.UnregisterRepo(workspace.Id, id);

        // TODO: Enqueue cleanup job to delete working directory (BE-012).

        return Results.NoContent();
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns the workspace identified by slug if the caller is a member, otherwise null.
    /// Collapses the "workspace doesn't exist" and "not a member" cases into a single 404 to
    /// avoid leaking existence of foreign workspaces.
    /// </summary>
    private static async Task<Workspace?> ResolveMemberWorkspaceAsync(
        ArchMindDbContext db,
        string slug,
        Guid userId,
        CancellationToken ct)
    {
        return await db.Workspaces
            .AsNoTracking()
            .Where(w => w.Slug == slug)
            .Join(
                db.WorkspaceMembers.AsNoTracking().Where(m => m.UserId == userId),
                w => w.Id,
                m => m.WorkspaceId,
                (w, _) => w)
            .FirstOrDefaultAsync(ct);
    }

    private static bool TryGetCurrentUserId(this HttpContext httpContext, out Guid userId)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out userId);
    }

    private static IResult Unauthenticated() =>
        Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotFound() =>
        Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
}
