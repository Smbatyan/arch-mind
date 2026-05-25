using Hangfire.Dashboard;

namespace ArchMind.Api.Auth;

/// <summary>
/// Authorizes Hangfire dashboard access to any authenticated user.
/// Role-based checks (admin-only) are post-MVP.
/// </summary>
public class SessionHangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User?.Identity?.IsAuthenticated == true;
    }
}
