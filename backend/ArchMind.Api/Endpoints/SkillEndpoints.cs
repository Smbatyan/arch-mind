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
/// BE-034: workspace-scoped skill management endpoints. All routes are mounted
/// under <c>/api/workspaces/{slug}/skills</c>, require an authenticated user
/// (enforced by the global fallback policy), and additionally verify
/// workspace membership before reading or writing.
/// </summary>
public static partial class SkillEndpoints
{
    // Slug rules per BE-034 spec: lowercase, alphanumeric + hyphens, must start
    // with alphanumeric, length 1..64. Matches: ^[a-z0-9][a-z0-9-]{0,63}$
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillNameRegex();

    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/skills");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapGet("/{id:guid}/revisions", ListRevisionsAsync);
        group.MapGet("/{id:guid}/revisions/{version:int}", GetRevisionAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record SkillListItemResponse(
        Guid Id,
        string Name,
        string Title,
        string Description,
        string[] Triggers,
        bool Enabled,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record SkillResponse(
        Guid Id,
        string Name,
        string Title,
        string Description,
        string Body,
        string[] Triggers,
        bool Enabled,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record CreateSkillRequest(
        string? Name,
        string? Title,
        string? Description,
        string? Body,
        string[]? Triggers,
        bool? Enabled);

    public sealed record UpdateSkillRequest(
        string? Title,
        string? Description,
        string? Body,
        string[]? Triggers,
        bool? Enabled,
        string? ChangeNote);

    public sealed record SkillRevisionListItemResponse(
        Guid Id,
        int Version,
        string Title,
        string Description,
        bool Enabled,
        string ChangeNote,
        DateTimeOffset CreatedAt);

    public sealed record SkillRevisionResponse(
        Guid Id,
        int Version,
        string Title,
        string Description,
        string Body,
        string[] Triggers,
        bool Enabled,
        string ChangeNote,
        DateTimeOffset CreatedAt);

    // ---------------------------------------------------------------------
    // Handlers
    // ---------------------------------------------------------------------
    private static async Task<IResult> ListAsync(
        string slug,
        ArchMindDbContext db,
        HttpContext httpContext,
        [FromQuery] bool? compact,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(httpContext, out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var query = db.Skills
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspace.Id)
            .OrderBy(s => s.Name);

        // `compact=true` strips the body field; the metadata shape is identical
        // either way (the SkillListItemResponse never carries body), so the flag
        // is presently a no-op aside from being honored as a documented option.
        _ = compact;

        var items = await query
            .Select(s => new SkillListItemResponse(
                s.Id, s.Name, s.Title, s.Description, s.Triggers, s.Enabled, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> GetAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(httpContext, out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var skill = await db.Skills
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == workspace.Id, ct);

        if (skill is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(MapSkill(skill));
    }

    private static async Task<IResult> CreateAsync(
        string slug,
        [FromBody] CreateSkillRequest req,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(httpContext, out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (req is null)
        {
            return Results.BadRequest(new { error = "missing body" });
        }

        var name = (req.Name ?? string.Empty).Trim();
        if (!SkillNameRegex().IsMatch(name))
        {
            return Results.BadRequest(new { error = "invalid name (must match ^[a-z0-9][a-z0-9-]{0,63}$)" });
        }

        var title = (req.Title ?? string.Empty).Trim();
        if (title.Length is < 1 or > 200)
        {
            return Results.BadRequest(new { error = "title must be 1..200 chars" });
        }

        var description = (req.Description ?? string.Empty).Trim();
        if (description.Length > 2000)
        {
            return Results.BadRequest(new { error = "description must be ≤ 2000 chars" });
        }

        var body = req.Body ?? string.Empty;
        var triggers = NormalizeTriggers(req.Triggers);
        var enabled = req.Enabled ?? true;

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var now = DateTimeOffset.UtcNow;
        var skill = new Skill
        {
            WorkspaceId = workspace.Id,
            Name = name,
            Title = title,
            Description = description,
            Body = body,
            Triggers = triggers,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var revision = new SkillRevision
        {
            SkillId = skill.Id, // set after SaveChanges if zero — EF assigns from default at save time
            WorkspaceId = workspace.Id,
            Version = 1,
            Title = title,
            Description = description,
            Body = body,
            Triggers = triggers,
            Enabled = enabled,
            ChangeNote = "initial revision",
            CreatedAt = now,
        };

        db.Skills.Add(skill);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "skill name already exists in this workspace" });
        }

        // Now that the Skill has a server-assigned Id we can wire it up and
        // persist the initial revision.
        revision.SkillId = skill.Id;
        db.SkillRevisions.Add(revision);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/workspaces/{slug}/skills/{skill.Id}",
            MapSkill(skill));
    }

    private static async Task<IResult> UpdateAsync(
        string slug,
        Guid id,
        [FromBody] UpdateSkillRequest req,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(httpContext, out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (req is null)
        {
            return Results.BadRequest(new { error = "missing body" });
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var skill = await db.Skills
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == workspace.Id, ct);
        if (skill is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var title = (req.Title ?? skill.Title).Trim();
        if (title.Length is < 1 or > 200)
        {
            return Results.BadRequest(new { error = "title must be 1..200 chars" });
        }

        var description = (req.Description ?? skill.Description).Trim();
        if (description.Length > 2000)
        {
            return Results.BadRequest(new { error = "description must be ≤ 2000 chars" });
        }

        var body = req.Body ?? skill.Body;
        var triggers = req.Triggers is null ? skill.Triggers : NormalizeTriggers(req.Triggers);
        var enabled = req.Enabled ?? skill.Enabled;
        var changeNote = (req.ChangeNote ?? string.Empty).Trim();
        if (changeNote.Length > 2000) changeNote = changeNote[..2000];

        skill.Title = title;
        skill.Description = description;
        skill.Body = body;
        skill.Triggers = triggers;
        skill.Enabled = enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow;

        // Compute next monotonic version. (skill_id, version) is unique so a
        // race here surfaces as a unique-violation we surface back to the
        // caller as 409.
        var maxVersion = await db.SkillRevisions
            .Where(r => r.SkillId == id)
            .MaxAsync(r => (int?)r.Version, ct) ?? 0;

        var revision = new SkillRevision
        {
            SkillId = skill.Id,
            WorkspaceId = workspace.Id,
            Version = maxVersion + 1,
            Title = title,
            Description = description,
            Body = body,
            Triggers = triggers,
            Enabled = enabled,
            ChangeNote = changeNote,
            CreatedAt = skill.UpdatedAt,
        };
        db.SkillRevisions.Add(revision);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "concurrent update — please retry" });
        }

        return Results.Ok(MapSkill(skill));
    }

    private static async Task<IResult> DeleteAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(httpContext, out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var skill = await db.Skills
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == workspace.Id, ct);
        if (skill is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        // SkillRevisions cascade via FK.
        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListRevisionsAsync(
        string slug,
        Guid id,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(httpContext, out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var exists = await db.Skills
            .AsNoTracking()
            .AnyAsync(s => s.Id == id && s.WorkspaceId == workspace.Id, ct);
        if (!exists)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var revisions = await db.SkillRevisions
            .AsNoTracking()
            .Where(r => r.SkillId == id && r.WorkspaceId == workspace.Id)
            .OrderByDescending(r => r.Version)
            .Select(r => new SkillRevisionListItemResponse(
                r.Id, r.Version, r.Title, r.Description, r.Enabled, r.ChangeNote, r.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(revisions);
    }

    private static async Task<IResult> GetRevisionAsync(
        string slug,
        Guid id,
        int version,
        ArchMindDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(httpContext, out var userId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        var revision = await db.SkillRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.SkillId == id && r.WorkspaceId == workspace.Id && r.Version == version,
                ct);
        if (revision is null)
        {
            return Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new SkillRevisionResponse(
            revision.Id,
            revision.Version,
            revision.Title,
            revision.Description,
            revision.Body,
            revision.Triggers,
            revision.Enabled,
            revision.ChangeNote,
            revision.CreatedAt));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static SkillResponse MapSkill(Skill skill) => new(
        skill.Id,
        skill.Name,
        skill.Title,
        skill.Description,
        skill.Body,
        skill.Triggers,
        skill.Enabled,
        skill.CreatedAt,
        skill.UpdatedAt);

    private static string[] NormalizeTriggers(string[]? input)
    {
        if (input is null || input.Length == 0) return Array.Empty<string>();
        var clean = new List<string>(input.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in input)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (trimmed.Length > 200) trimmed = trimmed[..200];
            if (seen.Add(trimmed))
            {
                clean.Add(trimmed);
            }
        }
        return clean.ToArray();
    }

    private static async Task<ArchMind.Core.Entities.Workspace?> ResolveMemberWorkspaceAsync(
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

    private static bool TryGetCurrentUserId(HttpContext httpContext, out Guid userId)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out userId);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg && pg.SqlState == "23505";
    }
}
