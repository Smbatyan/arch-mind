using System.Security.Claims;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Core.Models.Graph;
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
        "Database",
        "Queue",
        "Event",
        "Concept",
        "File",
        "Convention",
        "Capability",
        "Storage",
    };

    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/graph");

        group.MapGet("/labels", GetLabelsAsync);
        group.MapGet("/nodes", ListNodesByLabelAsync);
        group.MapGet("/nodes/{label}/{id:guid}", GetNodeAsync);
        group.MapGet("/visualization", GetVisualizationAsync);
        group.MapGet("/search", SearchNodesAsync);

        // Structural (AST) graph: served straight from combined_graph.json — no
        // semantic LLM extraction involved, so reads are free. Companion
        // /structural/search uses one cheap Haiku call to turn a NL question
        // into a keyword list, then matches deterministically on disk.
        group.MapGet("/structural", GetStructuralAsync);
        group.MapPost("/structural/search", SearchStructuralAsync);

        // BE-044: declared schema + drift report + live counts.
        group.MapGet("/schema-report", GetSchemaReportAsync);

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

    // ---------------------------------------------------------------------
    // Visual graph canvas data
    // ---------------------------------------------------------------------
    public sealed record VisualizationNodeDto(string Id, string Label, string? Name, string? RepoId);
    public sealed record VisualizationEdgeDto(string Id, string Source, string Target, string Label);
    public sealed record VisualizationDataDto(
        IReadOnlyList<VisualizationNodeDto> Nodes,
        IReadOnlyList<VisualizationEdgeDto> Edges,
        bool Truncated);

    private static async Task<IResult> GetVisualizationAsync(
        string slug,
        ArchMindDbContext db,
        IGraphReader graphReader,
        HttpContext httpContext,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
            return Unauthenticated();

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null) return NotFound();

        var nodeLimit = Math.Clamp(limit ?? 500, 1, 2000);
        var data = await graphReader.GetVisualizationDataAsync(workspace.Id, nodeLimit, ct);

        var nodeDtos = data.Nodes
            .Select(n => new VisualizationNodeDto(n.Id.ToString(), n.Label, n.Name, n.RepoId?.ToString()))
            .ToList();

        var idSet = new HashSet<string>(nodeDtos.Select(n => n.Id), StringComparer.Ordinal);

        var edgeDtos = data.Edges
            .Where(e => idSet.Contains(e.SourceId.ToString()) && idSet.Contains(e.TargetId.ToString()))
            .Select((e, i) => new VisualizationEdgeDto(
                $"e{i}",
                e.SourceId.ToString(),
                e.TargetId.ToString(),
                e.Label))
            .ToList();

        return Results.Ok(new VisualizationDataDto(nodeDtos, edgeDtos, data.Truncated));
    }

    // ---------------------------------------------------------------------
    // Full-text search across all vertex labels
    // ---------------------------------------------------------------------
    private static async Task<IResult> SearchNodesAsync(
        string slug,
        ArchMindDbContext db,
        IGraphReader graphReader,
        HttpContext httpContext,
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
            return Unauthenticated();

        if (string.IsNullOrWhiteSpace(q))
            return Results.Ok(Array.Empty<NodeSummaryResponse>());

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null) return NotFound();

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var take = Math.Clamp(limit ?? 25, 1, 100);
        var hits = await graphReader.SearchNodesByTextAsync(workspace.Id, tokens, take, ct);

        var response = hits
            .Select(h => new NodeSummaryResponse(h.Id, h.Label, h.Name))
            .ToList();

        return Results.Ok(response);
    }

    // ---------------------------------------------------------------------
    // BE-044: schema-report endpoint
    // ---------------------------------------------------------------------
    public sealed record NodeLabelDto(string Label, string[] Required, string[] Optional);

    public sealed record EdgeLabelDto(string Label, string[] FromLabels, string[] ToLabels);

    public sealed record DeclaredSchemaDto(
        IReadOnlyList<NodeLabelDto> Nodes,
        IReadOnlyList<EdgeLabelDto> Edges);

    public sealed record SchemaDriftDto(
        IReadOnlyList<string> MissingNodeLabels,
        IReadOnlyList<string> ExtraNodeLabels,
        IReadOnlyList<string> MissingEdgeLabels,
        IReadOnlyList<string> ExtraEdgeLabels,
        bool HasDrift);

    public sealed record GraphOverviewDto(
        IReadOnlyDictionary<string, int> NodeCounts,
        IReadOnlyDictionary<string, int> EdgeCounts);

    public sealed record SchemaReportResponse(
        DeclaredSchemaDto Declared,
        SchemaDriftDto Drift,
        GraphOverviewDto Counts);

    private static async Task<IResult> GetSchemaReportAsync(
        string slug,
        ArchMindDbContext db,
        IGraphReader graphReader,
        IGraphSchemaValidator validator,
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

        var declared = new DeclaredSchemaDto(
            GraphSchema.NodeLabels
                .Select(n => new NodeLabelDto(n.Label, n.Required, n.Optional))
                .ToList(),
            GraphSchema.EdgeLabels
                .Select(e => new EdgeLabelDto(e.Label, e.FromLabels, e.ToLabels))
                .ToList());

        var drift = await validator.CheckLiveSchemaAsync(ct);
        var driftDto = new SchemaDriftDto(
            drift.MissingNodeLabels,
            drift.ExtraNodeLabels,
            drift.MissingEdgeLabels,
            drift.ExtraEdgeLabels,
            drift.HasDrift);

        var overview = await graphReader.GetOverviewAsync(workspace.Id, ct);
        var countsDto = new GraphOverviewDto(overview.NodeCounts, overview.EdgeCounts);

        return Results.Ok(new SchemaReportResponse(declared, driftDto, countsDto));
    }

    // ---------------------------------------------------------------------
    // Structural (AST) graph DTOs + handlers
    // ---------------------------------------------------------------------
    public sealed record StructuralNodeDto(
        string Id,
        string Label,
        string Name,
        string? RepoId,
        string? SourceFile,
        int Degree);

    public sealed record StructuralEdgeDto(string Id, string Source, string Target, string Label);

    public sealed record StructuralDataDto(
        IReadOnlyList<StructuralNodeDto> Nodes,
        IReadOnlyList<StructuralEdgeDto> Edges,
        int TotalNodes,
        int TotalEdges,
        bool Truncated);

    public sealed record StructuralSearchRequest(string? Q);

    public sealed record StructuralSearchResponse(
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> NodeIds);

    public sealed record StructuralSearchKeywordsLlm(
        [property: System.Text.Json.Serialization.JsonPropertyName("keywords")] IReadOnlyList<string> Keywords);

    private static async Task<IResult> GetStructuralAsync(
        string slug,
        ArchMindDbContext db,
        IStructuralGraphService structural,
        HttpContext httpContext,
        [FromQuery] Guid? repoId,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
            return Unauthenticated();

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null) return NotFound();

        var nodeLimit = Math.Clamp(limit ?? 500, 1, 5000);
        var data = await structural.GetAsync(workspace.Id, repoId, nodeLimit, ct);

        var nodes = data.Nodes
            .Select(n => new StructuralNodeDto(n.Id, n.Label, n.Name, n.RepoId, n.SourceFile, n.Degree))
            .ToList();
        var edges = data.Edges
            .Select(e => new StructuralEdgeDto(e.Id, e.Source, e.Target, e.Label))
            .ToList();

        return Results.Ok(new StructuralDataDto(nodes, edges, data.TotalNodes, data.TotalEdges, data.Truncated));
    }

    private static async Task<IResult> SearchStructuralAsync(
        string slug,
        ArchMindDbContext db,
        IStructuralGraphService structural,
        ILlmRouter llmRouter,
        HttpContext httpContext,
        [FromBody] StructuralSearchRequest request,
        CancellationToken ct)
    {
        if (!httpContext.TryGetCurrentUserId(out var userId))
            return Unauthenticated();

        var workspace = await ResolveMemberWorkspaceAsync(db, slug, userId, ct);
        if (workspace is null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Q))
            return Results.Ok(new StructuralSearchResponse(Array.Empty<string>(), Array.Empty<string>()));

        // One cheap Haiku call → keyword list. We deliberately do NOT send graph
        // nodes to the model; only the user's question. This keeps the per-search
        // token cost in the hundreds, independent of repo size.
        const string systemPrompt =
            "You translate a developer's natural-language question into a list of " +
            "1-6 lowercase keywords/code identifiers most likely to appear in source " +
            "code names matching that question. Return JSON only.";

        var llm = await llmRouter.RouteStructuredAsync<StructuralSearchKeywordsLlm>(
            LlmTaskType.Classification,
            systemPrompt,
            userPrompt: request.Q!.Trim(),
            toolName: "extract_keywords",
            toolDescription: "Return a small array of code-identifier-like keywords for filtering a structural code graph.",
            jsonSchema:
                """
                {
                  "type": "object",
                  "properties": {
                    "keywords": {
                      "type": "array",
                      "items": { "type": "string" },
                      "minItems": 1,
                      "maxItems": 6
                    }
                  },
                  "required": ["keywords"]
                }
                """,
            ct);

        var keywords = llm.Output?.Keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList()
                       ?? new List<string>();

        var nodeIds = await structural.SearchByKeywordsAsync(workspace.Id, keywords, limit: 200, ct);

        return Results.Ok(new StructuralSearchResponse(keywords, nodeIds));
    }
}
