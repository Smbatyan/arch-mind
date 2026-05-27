using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
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

    // Org / user landing page: https://github.com/{ownerOrOrg} (single trailing segment)
    [GeneratedRegex(@"^https://github\.com/([^/]+)/?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubOrgUrlRegex();

    public static IEndpointRouteBuilder MapRepoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/repos");

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapGet("/{id:guid}/scans", ListScansAsync);
        group.MapPost("/{id:guid}/rescan", RescanAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPost("/discover", DiscoverOrgAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record CreateRepoRequest(string? GitHubUrl, string? DefaultBranch, string? PatToken, string? Name);

    public sealed record DiscoverOrgRequest(string? OrgUrl, string? PatToken);

    public sealed record DiscoveredRepo(
        string Name,
        string GitHubUrl,
        string DefaultBranch,
        bool Private,
        string? Description);

    public sealed record DiscoverOrgResponse(string Owner, IReadOnlyList<DiscoveredRepo> Repos);

    public sealed record CreateRepoResponse(
        Guid Id,
        string Name,
        string GitHubUrl,
        string DefaultBranch,
        string Status,
        DateTime CreatedAt);

    public sealed record RepoResponse(
        Guid Id,
        string Name,
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

        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0) name = DeriveNameFromUrl(githubUrl);
        if (name.Length > 200) name = name[..200];

        var repo = new Repo
        {
            Id = repoId,
            WorkspaceId = workspace.Id,
            Name = name,
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
            repo.Name,
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
                r.Name,
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
                r.Name,
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

    // -----------------------------------------------------------------------
    // POST /api/workspaces/{slug}/repos/discover
    // Expands a GitHub org / user URL into the full list of repositories
    // visible to the supplied PAT, so the UI can present them as a checklist
    // for bulk connection. Read-only — no DB writes happen here.
    // -----------------------------------------------------------------------
    private static async Task<IResult> DiscoverOrgAsync(
        string slug,
        [FromBody] DiscoverOrgRequest req,
        ArchMindDbContext db,
        IHttpClientFactory httpClientFactory,
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

        var orgUrl = req.OrgUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(orgUrl) || orgUrl.Length > 500)
        {
            return Results.BadRequest(new { error = "invalid org url" });
        }

        var orgMatch = GitHubOrgUrlRegex().Match(orgUrl);
        if (!orgMatch.Success)
        {
            return Results.BadRequest(new { error = "url must be https://github.com/<org-or-user>" });
        }
        var owner = orgMatch.Groups[1].Value;

        var pat = req.PatToken?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(pat) || pat.Length > 500)
        {
            return Results.BadRequest(new { error = "invalid pat token" });
        }

        var http = httpClientFactory.CreateClient("github");
        http.BaseAddress ??= new Uri("https://api.github.com/");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchMind/0.1");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // Try the org endpoint first; fall back to the user endpoint when
        // GitHub returns 404 (means `owner` is a user account, not an org).
        var (repos, error, statusCode) = await FetchOwnerReposAsync(http, owner, pat, ct);
        if (error is not null)
        {
            return Results.Json(new { error }, statusCode: statusCode);
        }

        return Results.Ok(new DiscoverOrgResponse(owner, repos));
    }

    private static async Task<(IReadOnlyList<DiscoveredRepo> Repos, string? Error, int StatusCode)>
        FetchOwnerReposAsync(HttpClient http, string owner, string pat, CancellationToken ct)
    {
        var all = new List<DiscoveredRepo>();
        // Try /orgs/{owner}/repos with pagination; on 404, retry under /users/{owner}/repos.
        var (success, statusCode) = await PageRepos($"orgs/{owner}/repos", all, http, pat, ct);
        if (!success && statusCode == StatusCodes.Status404NotFound)
        {
            all.Clear();
            (success, statusCode) = await PageRepos($"users/{owner}/repos", all, http, pat, ct);
        }
        if (!success)
        {
            return (Array.Empty<DiscoveredRepo>(), $"github api returned {statusCode}", statusCode == 401 ? 401 : 502);
        }
        return (all, null, StatusCodes.Status200OK);
    }

    private static async Task<(bool Success, int StatusCode)> PageRepos(
        string path,
        List<DiscoveredRepo> sink,
        HttpClient http,
        string pat,
        CancellationToken ct)
    {
        const int perPage = 100;
        for (var page = 1; page <= 20; page++) // cap at 2000 repos
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{path}?per_page={perPage}&page={page}&type=all&sort=updated");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, (int)resp.StatusCode);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (false, StatusCodes.Status502BadGateway);
            }

            var count = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                count++;
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var url = el.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                var branch = el.TryGetProperty("default_branch", out var b) ? b.GetString() ?? "main" : "main";
                var priv = el.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True;
                var desc = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (!string.IsNullOrEmpty(url))
                {
                    sink.Add(new DiscoveredRepo(name, url, branch, priv, desc));
                }
            }

            if (count < perPage) break; // last page
        }
        return (true, StatusCodes.Status200OK);
    }

    /// <summary>
    /// Pulls a display name from a GitHub URL. Strips the trailing <c>.git</c>
    /// suffix and any query string, then returns the final path segment
    /// (typically the repo name). Falls back to the full URL when parsing
    /// fails so the caller always gets a non-empty string.
    /// </summary>
    private static string DeriveNameFromUrl(string githubUrl)
    {
        if (string.IsNullOrWhiteSpace(githubUrl)) return string.Empty;
        var trimmed = githubUrl.Trim().TrimEnd('/');
        var qIdx = trimmed.IndexOf('?');
        if (qIdx >= 0) trimmed = trimmed[..qIdx];
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }
}
