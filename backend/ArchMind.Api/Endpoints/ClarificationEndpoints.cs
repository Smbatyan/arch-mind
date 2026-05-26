using System.Security.Claims;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// BE-039 (Sprint 5): minimal-API endpoints for browsing and resolving
/// Clarifications. Mounted under <c>/api/workspaces/{slug}/clarifications</c>.
/// All routes require cookie auth + workspace membership (collapsed to 404 so
/// foreign workspace existence isn't leaked).
/// </summary>
public static class ClarificationEndpoints
{
    public static IEndpointRouteBuilder MapClarificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/clarifications");

        group.MapGet("/", ListAsync);
        group.MapGet("/by-node/{name}", ListByNodeAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapPost("/{id:guid}/answer", AnswerAsync);
        group.MapPost("/{id:guid}/dismiss", DismissAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record ClarificationResponse(
        Guid Id,
        string Source,
        string Topic,
        string Question,
        string? Context,
        string?[] Choices,
        int Priority,
        string Status,
        string? Answer,
        DateTimeOffset? AnsweredAt,
        string[] RelatedFilePaths,
        string[] RelatedNodeNames,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record AnswerRequest(string? Answer, string? UserId);

    public sealed record DismissRequest(string? Reason);

    // ---------------------------------------------------------------------
    // Handlers
    // ---------------------------------------------------------------------
    private static async Task<IResult> ListAsync(
        string slug,
        ArchMindDbContext db,
        HttpContext httpContext,
        [FromQuery] string? status,
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

        var take = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Clarifications
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspace.Id);

        // status filter: open (default), answered, dismissed, all.
        var normalizedStatus = (status ?? "open").Trim().ToLowerInvariant();
        switch (normalizedStatus)
        {
            case "open":
                query = query.Where(c => c.Status == ClarificationStatus.Open);
                break;
            case "answered":
                query = query.Where(c => c.Status == ClarificationStatus.Answered);
                break;
            case "dismissed":
                query = query.Where(c => c.Status == ClarificationStatus.Dismissed);
                break;
            case "all":
                // no filter
                break;
            default:
                return Results.BadRequest(new { error = "invalid status" });
        }

        var rows = await query
            .OrderByDescending(c => c.Priority)
            .ThenByDescending(c => c.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToResponse).ToList());
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

        var row = await db.Clarifications
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == workspace.Id, ct);

        return row is null ? NotFound() : Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> AnswerAsync(
        string slug,
        Guid id,
        [FromBody] AnswerRequest req,
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

        var answerText = req?.Answer?.Trim() ?? string.Empty;
        if (answerText.Length == 0)
        {
            return Results.BadRequest(new { error = "answer required" });
        }

        var row = await db.Clarifications
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == workspace.Id, ct);
        if (row is null)
        {
            return NotFound();
        }

        if (row.Status != ClarificationStatus.Open)
        {
            return Results.Json(
                new { error = "clarification not open" },
                statusCode: StatusCodes.Status409Conflict);
        }

        // Resolve a stable string identifier for the answerer. Falls back
        // through NameIdentifier -> Email -> Identity.Name -> null.
        var answeredBy = ResolveAnsweredByUserId(httpContext);

        row.Status = ClarificationStatus.Answered;
        row.Answer = answerText;
        row.AnsweredByUserId = answeredBy;
        row.AnsweredAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> DismissAsync(
        string slug,
        Guid id,
        [FromBody] DismissRequest? req,
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

        var row = await db.Clarifications
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == workspace.Id, ct);
        if (row is null)
        {
            return NotFound();
        }

        if (row.Status != ClarificationStatus.Open)
        {
            return Results.Json(
                new { error = "clarification not open" },
                statusCode: StatusCodes.Status409Conflict);
        }

        var reason = req?.Reason?.Trim();
        row.Status = ClarificationStatus.Dismissed;
        row.Answer = string.IsNullOrEmpty(reason)
            ? "[dismissed]"
            : "[dismissed] " + reason;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> ListByNodeAsync(
        string slug,
        string name,
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

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "node name required" });
        }

        // Postgres array overlap: related_node_names && ARRAY[{name}]::text[].
        // FromSqlInterpolated needs a DbSet receiver; we then pull back into LINQ
        // for ordering/projection. The ORDER BY clause is in SQL so the LIMIT
        // applies after sort.
        var nameArray = new[] { name };
        var rows = await db.Clarifications
            .FromSqlInterpolated($@"
                SELECT * FROM clarifications
                WHERE workspace_id = {workspace.Id}
                  AND status IN ('Open', 'Answered')
                  AND related_node_names && {nameArray}::text[]
                ORDER BY priority DESC
                LIMIT 50")
            .AsNoTracking()
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToResponse).ToList());
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static ClarificationResponse ToResponse(Clarification c) =>
        new(
            c.Id,
            c.Source.ToString(),
            c.Topic,
            c.Question,
            c.Context,
            c.Choices ?? Array.Empty<string?>(),
            c.Priority,
            c.Status.ToString(),
            c.Answer,
            c.AnsweredAt,
            c.RelatedFilePaths ?? Array.Empty<string>(),
            c.RelatedNodeNames ?? Array.Empty<string>(),
            c.CreatedAt,
            c.UpdatedAt);

    private static string? ResolveAnsweredByUserId(HttpContext httpContext)
    {
        var user = httpContext.User;
        if (user is null) return null;

        var nameId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(nameId)) return nameId;

        var email = user.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email)) return email;

        var name = user.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // Duplicated rather than refactored cross-file per Sprint scope guidance.
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
