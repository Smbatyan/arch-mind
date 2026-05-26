using System.Globalization;
using System.Security.Claims;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// BE-043: workspace-level reporting endpoints. All routes are mounted under
/// <c>/api/workspaces/{slug}/report</c>, require an authenticated session, and
/// 404 (not 403) when the caller is not a workspace member — mirroring the
/// existing pattern in <see cref="GraphEndpoints"/> / <see cref="RepoEndpoints"/>.
///
/// Decimal costs are serialized as strings ("12.345678") so the wire format
/// preserves the full Postgres <c>numeric(12,6)</c> precision regardless of
/// what double conversion the consumer applies.
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces/{slug}/report");

        group.MapGet("/summary", GetSummaryAsync);
        group.MapGet("/scans", ListScansAsync);
        group.MapGet("/scans/{scanRunId:guid}", GetScanAsync);
        group.MapGet("/llm-spend", GetLlmSpendAsync);
        group.MapGet("/mcp-activity", GetMcpActivityAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record WorkspaceRef(Guid Id, string Slug, string Name);

    public sealed record ReposBlock(int Total, int Active, DateTime? LastScanAt);

    public sealed record GraphBlock(
        IReadOnlyDictionary<string, int> NodeCounts,
        IReadOnlyDictionary<string, int> EdgeCounts);

    public sealed record ExtractionsBlock(int TotalFiles, double CachedPct);

    public sealed record ClarificationsBlock(int Open, int Answered, int Dismissed);

    public sealed record SkillsBlock(int Count);

    public sealed record LlmPurposeStats(
        long TotalCalls,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        string CostUsd,
        double CacheHitPct);

    public sealed record LlmBlock(
        string TotalCostUsd,
        long TotalCalls,
        double CacheHitPct,
        IReadOnlyDictionary<string, LlmPurposeStats> ByPurpose);

    public sealed record McpBlock(
        long TotalCalls,
        int P50LatencyMs,
        int P95LatencyMs,
        double ErrorRatePct);

    public sealed record SummaryResponse(
        WorkspaceRef Workspace,
        ReposBlock Repos,
        GraphBlock Graph,
        ExtractionsBlock Extractions,
        ClarificationsBlock Clarifications,
        SkillsBlock Skills,
        LlmBlock Llm,
        McpBlock Mcp);

    public sealed record ScanRunListItem(
        Guid Id,
        Guid RepoId,
        string Kind,
        string Status,
        DateTime StartedAt,
        DateTime? CompletedAt,
        int FilesScanned,
        int FilesEnqueued,
        int GraphifyNodes,
        int GraphifyEdges,
        long TotalTokens,
        string TotalCostUsd,
        string? ErrorMessage);

    public sealed record ScanRunDetail(
        Guid Id,
        Guid RepoId,
        string Kind,
        string Status,
        DateTime StartedAt,
        DateTime? CompletedAt,
        long? DurationMs,
        string? FromSha,
        string? ToSha,
        int FilesScanned,
        int FilesEnqueued,
        int GraphifyNodes,
        int GraphifyEdges,
        long TotalTokens,
        string TotalCostUsd,
        string? ErrorMessage,
        DateTime CreatedAt);

    public sealed record LlmSpendDay(string Date, string Cost, int Calls, int CachedCalls);

    public sealed record LlmSpendResponse(IReadOnlyList<LlmSpendDay> Days);

    public sealed record McpActivityDay(string Date, int Calls, int Errors, int P95);

    public sealed record McpActivityResponse(IReadOnlyList<McpActivityDay> Days);

    // ---------------------------------------------------------------------
    // GET /summary
    // ---------------------------------------------------------------------
    private static async Task<IResult> GetSummaryAsync(
        string slug,
        ArchMindDbContext db,
        IGraphReader graphReader,
        IDbConnectionFactory connectionFactory,
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

        var wid = workspace.Id;

        // Repos: total + active + lastScanAt across all repos in workspace.
        var repos = await db.Repos
            .AsNoTracking()
            .Where(r => r.WorkspaceId == wid)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Active = g.Count(r => r.Status == "active"),
            })
            .FirstOrDefaultAsync(ct);

        var lastScanAt = await db.ScanRuns
            .AsNoTracking()
            .Where(s => s.WorkspaceId == wid)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => (DateTime?)s.StartedAt)
            .FirstOrDefaultAsync(ct);

        var reposBlock = new ReposBlock(
            Total: repos?.Total ?? 0,
            Active: repos?.Active ?? 0,
            LastScanAt: lastScanAt);

        // Graph counts from the AGE-backed reader.
        Core.Models.Graph.GraphOverview overview;
        try
        {
            overview = await graphReader.GetOverviewAsync(wid, ct);
        }
        catch
        {
            // If AGE is unavailable, return empty counts rather than failing
            // the whole summary call — the summary remains useful without it.
            overview = new Core.Models.Graph.GraphOverview(
                new Dictionary<string, int>(),
                new Dictionary<string, int>());
        }

        var graphBlock = new GraphBlock(overview.NodeCounts, overview.EdgeCounts);

        // FileExtractions: count + cache-hit ratio (approx) — we don't have a
        // cache-hit flag on FileExtraction itself, so derive the ratio from
        // llm_call_logs where Purpose starts with "Extraction".
        var totalFiles = await db.FileExtractions
            .AsNoTracking()
            .Where(f => f.WorkspaceId == wid)
            .CountAsync(ct);

        double cachedPct = 0;
        if (totalFiles > 0)
        {
            var extractionCalls = await db.LlmCallLogs
                .AsNoTracking()
                .Where(l => l.WorkspaceId == wid && l.Purpose.StartsWith("Extraction"))
                .GroupBy(_ => 1)
                .Select(g => new { Total = g.Count(), Cached = g.Count(l => l.CacheHit) })
                .FirstOrDefaultAsync(ct);

            if (extractionCalls is not null && extractionCalls.Total > 0)
            {
                cachedPct = Math.Round(extractionCalls.Cached * 100.0 / extractionCalls.Total, 2);
            }
        }

        var extractionsBlock = new ExtractionsBlock(totalFiles, cachedPct);

        // Clarifications by status.
        var clarStats = await db.Clarifications
            .AsNoTracking()
            .Where(c => c.WorkspaceId == wid)
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int clarOpen = 0, clarAnswered = 0, clarDismissed = 0;
        foreach (var row in clarStats)
        {
            switch (row.Status)
            {
                case ClarificationStatus.Open: clarOpen = row.Count; break;
                case ClarificationStatus.Answered: clarAnswered = row.Count; break;
                case ClarificationStatus.Dismissed: clarDismissed = row.Count; break;
            }
        }

        var clarificationsBlock = new ClarificationsBlock(clarOpen, clarAnswered, clarDismissed);

        var skillCount = await db.Skills
            .AsNoTracking()
            .CountAsync(s => s.WorkspaceId == wid, ct);

        // LLM totals + by-purpose breakdown. Single EF group-by query then
        // post-process to compute the per-purpose cache hit pct.
        var llmAgg = await db.LlmCallLogs
            .AsNoTracking()
            .Where(l => l.WorkspaceId == wid)
            .GroupBy(l => l.Purpose)
            .Select(g => new
            {
                Purpose = g.Key,
                Calls = (long)g.Count(),
                InputTokens = (long)g.Sum(x => (long)x.InputTokens),
                OutputTokens = (long)g.Sum(x => (long)x.OutputTokens),
                CacheRead = (long)g.Sum(x => (long)x.CacheReadTokens),
                CacheWrite = (long)g.Sum(x => (long)x.CacheWriteTokens),
                Cost = g.Sum(x => x.CostUsd),
                Cached = (long)g.Count(x => x.CacheHit),
            })
            .ToListAsync(ct);

        long llmTotalCalls = 0;
        long llmTotalCached = 0;
        decimal llmTotalCost = 0m;
        var byPurpose = new Dictionary<string, LlmPurposeStats>(StringComparer.Ordinal);
        foreach (var p in llmAgg)
        {
            llmTotalCalls += p.Calls;
            llmTotalCached += p.Cached;
            llmTotalCost += p.Cost;

            double pct = p.Calls > 0 ? Math.Round(p.Cached * 100.0 / p.Calls, 2) : 0;
            byPurpose[p.Purpose] = new LlmPurposeStats(
                TotalCalls: p.Calls,
                InputTokens: p.InputTokens,
                OutputTokens: p.OutputTokens,
                CacheReadTokens: p.CacheRead,
                CacheWriteTokens: p.CacheWrite,
                CostUsd: FormatCost(p.Cost),
                CacheHitPct: pct);
        }

        double llmHitPct = llmTotalCalls > 0
            ? Math.Round(llmTotalCached * 100.0 / llmTotalCalls, 2)
            : 0;

        var llmBlock = new LlmBlock(
            TotalCostUsd: FormatCost(llmTotalCost),
            TotalCalls: llmTotalCalls,
            CacheHitPct: llmHitPct,
            ByPurpose: byPurpose);

        // MCP percentiles via Dapper + Postgres percentile_cont. Single query.
        var mcpBlock = await QueryMcpStatsAsync(connectionFactory, wid, ct);

        var response = new SummaryResponse(
            Workspace: new WorkspaceRef(workspace.Id, workspace.Slug, workspace.Name),
            Repos: reposBlock,
            Graph: graphBlock,
            Extractions: extractionsBlock,
            Clarifications: clarificationsBlock,
            Skills: new SkillsBlock(skillCount),
            Llm: llmBlock,
            Mcp: mcpBlock);

        return Results.Ok(response);
    }

    private static async Task<McpBlock> QueryMcpStatsAsync(
        IDbConnectionFactory factory,
        Guid workspaceId,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT
              COUNT(*)::bigint AS total_calls,
              COALESCE(percentile_cont(0.5) WITHIN GROUP (ORDER BY latency_ms), 0)::int AS p50,
              COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms), 0)::int AS p95,
              SUM(CASE WHEN status_code >= 400 THEN 1 ELSE 0 END)::bigint AS errors
            FROM mcp_telemetry
            WHERE workspace_id = @wid;
        ";

        await using var conn = await factory.OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleAsync<McpStatsRow>(
            new CommandDefinition(sql, new { wid = workspaceId }, cancellationToken: ct)).ConfigureAwait(false);

        double errorPct = row.total_calls > 0
            ? Math.Round(row.errors * 100.0 / row.total_calls, 2)
            : 0;

        return new McpBlock(row.total_calls, row.p50, row.p95, errorPct);
    }

    private sealed class McpStatsRow
    {
        public long total_calls { get; set; }
        public int p50 { get; set; }
        public int p95 { get; set; }
        public long errors { get; set; }
    }

    // ---------------------------------------------------------------------
    // GET /scans
    // ---------------------------------------------------------------------
    private static async Task<IResult> ListScansAsync(
        string slug,
        ArchMindDbContext db,
        HttpContext httpContext,
        [FromQuery] Guid? repoId,
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

        var take = Math.Clamp(limit ?? 20, 1, 200);

        var query = db.ScanRuns
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspace.Id);

        if (repoId is { } rid && rid != Guid.Empty)
        {
            query = query.Where(s => s.RepoId == rid);
        }

        var rows = await query
            .OrderByDescending(s => s.StartedAt)
            .Take(take)
            .ToListAsync(ct);

        var items = rows
            .Select(s => new ScanRunListItem(
                s.Id, s.RepoId, s.Kind, s.Status, s.StartedAt, s.CompletedAt,
                s.FilesScanned, s.FilesEnqueued, s.GraphifyNodes, s.GraphifyEdges,
                s.TotalTokens, FormatCost(s.TotalCostUsd), s.ErrorMessage))
            .ToList();

        return Results.Ok(items);
    }

    // ---------------------------------------------------------------------
    // GET /scans/{scanRunId}
    // ---------------------------------------------------------------------
    private static async Task<IResult> GetScanAsync(
        string slug,
        Guid scanRunId,
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

        var s = await db.ScanRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.WorkspaceId == workspace.Id && x.Id == scanRunId, ct);

        if (s is null)
        {
            return NotFound();
        }

        long? durationMs = s.CompletedAt is { } completed
            ? (long)Math.Max(0, (completed - s.StartedAt).TotalMilliseconds)
            : null;

        var detail = new ScanRunDetail(
            s.Id, s.RepoId, s.Kind, s.Status, s.StartedAt, s.CompletedAt,
            durationMs, s.FromSha, s.ToSha,
            s.FilesScanned, s.FilesEnqueued, s.GraphifyNodes, s.GraphifyEdges,
            s.TotalTokens, FormatCost(s.TotalCostUsd), s.ErrorMessage, s.CreatedAt);

        return Results.Ok(detail);
    }

    // ---------------------------------------------------------------------
    // GET /llm-spend?days=30
    // ---------------------------------------------------------------------
    private static async Task<IResult> GetLlmSpendAsync(
        string slug,
        ArchMindDbContext db,
        IDbConnectionFactory factory,
        HttpContext httpContext,
        [FromQuery] int? days,
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

        var window = Math.Clamp(days ?? 30, 1, 90);

        const string sql = @"
            SELECT
              to_char(date_trunc('day', created_at), 'YYYY-MM-DD') AS bucket,
              SUM(cost_usd)::numeric(14,6)                          AS cost,
              COUNT(*)::int                                         AS calls,
              SUM(CASE WHEN cache_hit THEN 1 ELSE 0 END)::int       AS cached
            FROM llm_call_logs
            WHERE workspace_id = @wid
              AND created_at >= NOW() - (@days::int * INTERVAL '1 day')
            GROUP BY date_trunc('day', created_at)
            ORDER BY bucket ASC;
        ";

        await using var conn = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<LlmSpendRow>(
            new CommandDefinition(sql, new { wid = workspace.Id, days = window }, cancellationToken: ct))
            .ConfigureAwait(false);

        var days_list = rows
            .Select(r => new LlmSpendDay(r.bucket, FormatCost(r.cost), r.calls, r.cached))
            .ToList();

        return Results.Ok(new LlmSpendResponse(days_list));
    }

    private sealed class LlmSpendRow
    {
        public string bucket { get; set; } = string.Empty;
        public decimal cost { get; set; }
        public int calls { get; set; }
        public int cached { get; set; }
    }

    // ---------------------------------------------------------------------
    // GET /mcp-activity?days=7
    // ---------------------------------------------------------------------
    private static async Task<IResult> GetMcpActivityAsync(
        string slug,
        ArchMindDbContext db,
        IDbConnectionFactory factory,
        HttpContext httpContext,
        [FromQuery] int? days,
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

        var window = Math.Clamp(days ?? 7, 1, 90);

        const string sql = @"
            SELECT
              to_char(date_trunc('day', created_at), 'YYYY-MM-DD')                     AS bucket,
              COUNT(*)::int                                                            AS calls,
              SUM(CASE WHEN status_code >= 400 THEN 1 ELSE 0 END)::int                 AS errors,
              COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms), 0)::int AS p95
            FROM mcp_telemetry
            WHERE workspace_id = @wid
              AND created_at >= NOW() - (@days::int * INTERVAL '1 day')
            GROUP BY date_trunc('day', created_at)
            ORDER BY bucket ASC;
        ";

        await using var conn = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<McpActivityRow>(
            new CommandDefinition(sql, new { wid = workspace.Id, days = window }, cancellationToken: ct))
            .ConfigureAwait(false);

        var days_list = rows
            .Select(r => new McpActivityDay(r.bucket, r.calls, r.errors, r.p95))
            .ToList();

        return Results.Ok(new McpActivityResponse(days_list));
    }

    private sealed class McpActivityRow
    {
        public string bucket { get; set; } = string.Empty;
        public int calls { get; set; }
        public int errors { get; set; }
        public int p95 { get; set; }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static string FormatCost(decimal value) =>
        // Six decimals matches the Postgres column scale; invariant culture
        // ensures a "." separator regardless of server locale.
        value.ToString("0.000000", CultureInfo.InvariantCulture);

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
