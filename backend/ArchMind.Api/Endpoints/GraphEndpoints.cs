using System.Security.Claims;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for read-only graph browsing in the admin UI.
/// All routes are mounted under /api/workspaces/{slug}/graph and require
/// an authenticated session + workspace membership.
///
/// FE-010: backs the list-based graph browser. Visual graph rendering and
/// MCP-based access come later (Sprint 4+). These endpoints are intentionally
/// a thin proxy over <see cref="IGraphReader"/> — no caching, no aggregation.
/// </summary>
public static class GraphEndpoints
{
    // ---------------------------------------------------------------------
    // Allowlist of vertex labels accepted by /nodes endpoints.
    //
    // Source of truth lives in ArchMind.Infrastructure.Graph.GraphLabels
    // (internal). Duplicated here so the API layer can validate before
    // calling the reader without taking a friend-assembly dependency on
    // infrastructure internals. Keep these in lockstep — if you add a
    // label to GraphLabels.Vertex, add it here too.
    // ---------------------------------------------------------------------
    private static readonly HashSet<string> AllowedVertexLabels = new(StringComparer.Ordinal)
    {
        "Service",
        "Endpoint",
        "Event",
        "Topic",
        "Storage",
        "Dependency",
        "Convention",
        "Capability",
        "Skill",
        "Team",
    };

    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/graph");

        group.MapGet("/labels", GetLabelsAsync);
        group.MapGet("/nodes", ListNodesByLabelAsync);
        group.MapGet("/nodes/{label}/{id:guid}", GetNodeAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record LabelCount(string Label, int Count);

    public sealed record GraphLabelsResponse(
        IReadOnlyList<LabelCount> Vertices,
        IReadOnlyList<LabelCount> Edges);

    public sealed record NodeSummaryResponse(Guid Id, string Label, string? Name);

    public sealed record EdgeRefResponse(
        string Label,
        Guid OtherNodeId,
        string OtherNodeLabel,
        string? OtherNodeName,
        IReadOnlyDictionary<string, object?> Properties);

    public sealed record NodeDetailResponse(
        Guid Id,
        string Label,
        IReadOnlyDictionary<string, object?> Properties,
        IReadOnlyList<EdgeRefResponse> IncomingEdges,
        IReadOnlyList<EdgeRefResponse> OutgoingEdges);

    // ---------------------------------------------------------------------
    // Handlers
    // ---------------------------------------------------------------------
    private static async Task<IResult> GetLabelsAsync(
        string slug,
        ArchMindDbContext db,
        IGraphReader graphReader,
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

        var vertexCounts = await graphReader.CountNodesPerLabelAsync(workspace.Id, ct);

        var vertices = vertexCounts
            .Select(kv => new LabelCount(kv.Key, kv.Value))
            .OrderBy(v => v.Label, StringComparer.Ordinal)
            .ToList();

        // TODO: expose edge counts via IGraphReader. For now emit an empty
        // list — the UI shows the Edges section as read-only and tolerates
        // an empty edge list (renders nothing under that heading).
        var edges = Array.Empty<LabelCount>();

        return Results.Ok(new GraphLabelsResponse(vertices, edges));
    }

    private static async Task<IResult> ListNodesByLabelAsync(
        string slug,
        ArchMindDbContext db,
        IGraphReader graphReader,
        HttpContext httpContext,
        [FromQuery] string? label,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        if (string.IsNullOrWhiteSpace(label) || !AllowedVertexLabels.Contains(label))
        {
            return Results.BadRequest(new { error = "invalid label" });
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        var take = Math.Clamp(limit ?? 200, 1, 5000);

        var summaries = await graphReader.ListNodesByLabelAsync(workspace.Id, label, take, ct);

        var response = summaries
            .Select(s => new NodeSummaryResponse(s.Id, s.Label, s.Name))
            .ToList();

        return Results.Ok(response);
    }

    private static async Task<IResult> GetNodeAsync(
        string slug,
        string label,
        Guid id,
        ArchMindDbContext db,
        IGraphReader graphReader,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
        {
            return Unauthenticated();
        }

        if (string.IsNullOrWhiteSpace(label) || !AllowedVertexLabels.Contains(label))
        {
            return Results.BadRequest(new { error = "invalid label" });
        }

        if (id == Guid.Empty)
        {
            return Results.BadRequest(new { error = "invalid id" });
        }

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        var detail = await graphReader.GetNodeAsync(workspace.Id, label, id, ct);
        if (detail is null)
        {
            return NotFound();
        }

        var response = new NodeDetailResponse(
            detail.Id,
            detail.Label,
            detail.Properties,
            detail.IncomingEdges
                .Select(e => new EdgeRefResponse(
                    e.Label, e.OtherNodeId, e.OtherNodeLabel, e.OtherNodeName, e.Properties))
                .ToList(),
            detail.OutgoingEdges
                .Select(e => new EdgeRefResponse(
                    e.Label, e.OtherNodeId, e.OtherNodeLabel, e.OtherNodeName, e.Properties))
                .ToList());

        return Results.Ok(response);
    }

    // ---------------------------------------------------------------------
    // Helpers — mirrors the pattern in RepoEndpoints. Resolving workspace +
    // membership in a single query keeps "workspace doesn't exist" and "not
    // a member" indistinguishable from a 404, so we don't leak the existence
    // of foreign workspaces.
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

    private static IResult Unauthenticated() =>
        Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotFound() =>
        Results.Json(new { error = "not found" }, statusCode: StatusCodes.Status404NotFound);
}
