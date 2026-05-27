using ArchMind.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// Lightweight read-only Hangfire job state snapshot for the admin UI.
/// Surfaces queue depth so users know background extraction is still running
/// before the graph reflects their latest scan.
///
/// Workspace-agnostic on purpose — Hangfire job rows don't carry a workspace
/// scope, so we can't break this down per workspace without parsing each
/// invocation's argument blob. The UI just needs "is anything pending" plus
/// rough counts.
/// </summary>
public static class JobsEndpoints
{
    public sealed record JobStatusResponse(
        int Enqueued,
        int Processing,
        int Scheduled,
        int FailedLastHour,
        int SucceededLastHour,
        DateTime AsOf);

    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs/status", GetStatusAsync)
            .AllowAnonymous(); // queue depth isn't sensitive; UI polls without a session sometimes
        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        ArchMindDbContext db, CancellationToken ct)
    {
        // Read Hangfire's job table via raw SQL — Hangfire owns the schema and
        // we don't want to map it as EF entities just for a status pill.
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                  COUNT(*) FILTER (WHERE statename = 'Enqueued')                                            AS enqueued,
                  COUNT(*) FILTER (WHERE statename = 'Processing')                                          AS processing,
                  COUNT(*) FILTER (WHERE statename = 'Scheduled')                                           AS scheduled,
                  COUNT(*) FILTER (WHERE statename = 'Failed'    AND createdat > now() - interval '1 hour') AS failed_hour,
                  COUNT(*) FILTER (WHERE statename = 'Succeeded' AND createdat > now() - interval '1 hour') AS succeeded_hour
                FROM hangfire.job;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return Results.Ok(new JobStatusResponse(0, 0, 0, 0, 0, DateTime.UtcNow));
            }

            var resp = new JobStatusResponse(
                Enqueued: ReadInt(reader, 0),
                Processing: ReadInt(reader, 1),
                Scheduled: ReadInt(reader, 2),
                FailedLastHour: ReadInt(reader, 3),
                SucceededLastHour: ReadInt(reader, 4),
                AsOf: DateTime.UtcNow);
            return Results.Ok(resp);
        }
        catch
        {
            // Hangfire schema may not be available yet on a fresh DB.
            return Results.Ok(new JobStatusResponse(0, 0, 0, 0, 0, DateTime.UtcNow));
        }
    }

    private static int ReadInt(System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0;
        var v = r.GetValue(i);
        return v switch
        {
            int n => n,
            long l => l > int.MaxValue ? int.MaxValue : (int)l,
            _ => int.TryParse(v?.ToString(), out var n) ? n : 0
        };
    }
}
