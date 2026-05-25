using Serilog.Context;

namespace ArchMind.Api.Middleware;

/// <summary>
/// Pushes a placeholder WorkspaceId into Serilog's LogContext for every request.
/// Replaced with a real workspace resolver in BE-005+.
/// </summary>
public class WorkspaceLogContextMiddleware
{
    private readonly RequestDelegate _next;

    public WorkspaceLogContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        using (LogContext.PushProperty("WorkspaceId", "n/a"))
        {
            await _next(context);
        }
    }
}
