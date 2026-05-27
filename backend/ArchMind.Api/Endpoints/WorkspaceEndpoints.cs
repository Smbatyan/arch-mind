using System.Security.Claims;
using System.Text.RegularExpressions;
using ArchMind.Core.Abstractions;
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

        // BE-028: workspace API keys for MCP bearer-token auth.
        group.MapPost("/{slug}/api-keys", CreateApiKeyAsync);
        group.MapGet("/{slug}/api-keys", ListApiKeysAsync);
        group.MapDelete("/{slug}/api-keys/{id:guid}", RevokeApiKeyAsync);

        // Claude Code custom command file — public, no auth required.
        group.MapGet("/{slug}/claude-commands", GetClaudeCommandsAsync)
            .AllowAnonymous();

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
    // BE-028: API key DTOs + handlers
    // ---------------------------------------------------------------------
    public sealed record CreateApiKeyRequest(string? Name);
    public sealed record CreateApiKeyResponse(
        Guid Id,
        string Name,
        string Plaintext,
        string Prefix,
        DateTimeOffset CreatedAt);
    public sealed record ApiKeyResponse(
        Guid Id,
        string Name,
        string Prefix,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastUsedAt,
        DateTimeOffset? RevokedAt);

    private static async Task<IResult> CreateApiKeyAsync(
        string slug,
        [FromBody] CreateApiKeyRequest req,
        ArchMindDbContext db,
        IApiKeyService apiKeys,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var name = req?.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
        {
            return Results.BadRequest(new { error = "invalid name" });
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var (entity, plaintext) = await apiKeys.CreateAsync(workspace.Id, name, ct);

        return Results.Ok(new CreateApiKeyResponse(
            entity.Id,
            entity.Name,
            plaintext,
            entity.KeyPrefix,
            entity.CreatedAt));
    }

    private static async Task<IResult> ListApiKeysAsync(
        string slug,
        ArchMindDbContext db,
        IApiKeyService apiKeys,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var keys = await apiKeys.ListAsync(workspace.Id, ct);
        var response = keys
            .Select(k => new ApiKeyResponse(k.Id, k.Name, k.KeyPrefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt))
            .ToList();

        return Results.Ok(response);
    }

    private static async Task<IResult> RevokeApiKeyAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        IApiKeyService apiKeys,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        // Verify the key actually belongs to this workspace before revoking.
        var owned = await db.WorkspaceApiKeys
            .AsNoTracking()
            .AnyAsync(k => k.Id == id && k.WorkspaceId == workspace.Id, ct);
        if (!owned)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        await apiKeys.RevokeAsync(id, ct);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------------
    // Claude Code commands file (public)
    // ---------------------------------------------------------------------
    private static async Task<IResult> GetClaudeCommandsAsync(
        string slug,
        ArchMindDbContext db,
        CancellationToken ct)
    {
        var exists = await db.Workspaces
            .AsNoTracking()
            .AnyAsync(w => w.Slug == slug, ct);

        if (!exists)
        {
            return Results.NotFound();
        }

        var content =
            $"""
            Use ArchMind MCP tools to: $ARGUMENTS
            """;

        return Results.Text(content, contentType: "text/plain", contentEncoding: System.Text.Encoding.UTF8);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
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

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Postgres unique_violation SQLSTATE = 23505
        return ex.InnerException is PostgresException pg && pg.SqlState == "23505";
    }
}
