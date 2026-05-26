using ArchMind.Api.Smoke;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// BE-046: <c>GET /smoke</c> — anonymous health probe that exercises the
/// critical-path dependencies (DB, AGE, Hangfire, Anthropic, disk) and
/// returns a structured JSON report. HTTP 200 when overall status is
/// <c>ok</c> or <c>degraded</c>; HTTP 503 when overall status is <c>fail</c>
/// so external orchestrators (docker healthcheck, k8s readiness) can act on it.
/// </summary>
public static class SmokeEndpoints
{
    public static IEndpointRouteBuilder MapSmokeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/smoke", async (HttpContext ctx, CancellationToken ct) =>
            {
                var report = await SmokeRunner.RunAsync(ctx.RequestServices, ct);

                var statusCode = report.Status == "fail"
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK;

                return Results.Json(new
                {
                    status = report.Status,
                    checks = report.Checks.Select(c => new
                    {
                        name = c.Name,
                        status = c.Status,
                        required = c.Required,
                        durationMs = c.DurationMs,
                        error = c.Error,
                    }),
                }, statusCode: statusCode);
            })
            .AllowAnonymous();

        return app;
    }
}
