using System.Security.Claims;
using System.Text.RegularExpressions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for workspace creation, listing, and detail lookup.
/// All routes are mounted under /api/workspaces and require authentication
/// (enforced by the global fallback policy).
/// </summary>
public static partial class WorkspaceEndpoints
{
    // Slug rules: lowercase, alphanumeric + hyphens, 3-50 chars,
    // must begin and end with an alphanumeric character.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,48}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces");

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{slug}", GetBySlugAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record CreateWorkspaceRequest(string Name, string Slug);
    public sealed record WorkspaceResponse(Guid Id, string Slug, string Name, DateTime CreatedAt);
    public sealed record WorkspaceMembershipResponse(Guid Id, string Slug, string Name, string Role, DateTime CreatedAt);

    // ---------------------------------------------------------------------
    // Handlers
    // ---------------------------------------------------------------------
    private static async Task<IResult> CreateAsync(
        [FromBody] CreateWorkspaceRequest req,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var name = req.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
        {
            return Results.BadRequest(new { error = "invalid name" });
        }

        var slug = req.Slug ?? string.Empty;
        if (!SlugRegex().IsMatch(slug))
        {
            return Results.BadRequest(new { error = "invalid slug" });
        }

        var workspace = new Workspace
        {
            Slug = slug,
            Name = name,
        };
        var membership = new WorkspaceMember
        {
            Workspace = workspace,
            UserId = userId,
            Role = "owner",
        };

        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(membership);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "slug already taken" });
        }

        return Results.Ok(new WorkspaceResponse(workspace.Id, workspace.Slug, workspace.Name, workspace.CreatedAt));
    }

    private static async Task<IResult> ListAsync(
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var memberships = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(
                db.Workspaces.AsNoTracking(),
                m => m.WorkspaceId,
                w => w.Id,
                (m, w) => new WorkspaceMembershipResponse(w.Id, w.Slug, w.Name, m.Role, w.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(memberships);
    }

    private static async Task<IResult> GetBySlugAsync(
        string slug,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await db.Workspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Slug == slug, ct);

        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var isMember = await db.WorkspaceMembers
            .AsNoTracking()
            .AnyAsync(m => m.WorkspaceId == workspace.Id && m.UserId == userId, ct);

        if (!isMember)
        {
            return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new WorkspaceResponse(workspace.Id, workspace.Slug, workspace.Name, workspace.CreatedAt));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static bool TryGetCurrentUserId(this HttpContext httpContext, out Guid userId)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out userId);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Postgres unique_violation SQLSTATE = 23505
        return ex.InnerException is PostgresException pg && pg.SqlState == "23505";
    }
}
