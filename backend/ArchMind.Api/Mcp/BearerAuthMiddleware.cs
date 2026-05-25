using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArchMind.Api.Mcp;

/// <summary>
/// Validates the <c>Authorization: Bearer &lt;api_key&gt;</c> header on MCP requests by hashing
/// the plaintext token, looking up the matching <c>workspace_api_keys</c> row via
/// <see cref="IApiKeyService.ValidateAsync"/>, and confirming the key belongs to the workspace
/// resolved from the route slug.
/// </summary>
/// <remarks>
/// The resolved workspace id and api-key id are stashed in <see cref="HttpContext.Items"/> under
/// <see cref="WorkspaceIdKey"/> / <see cref="ApiKeyIdKey"/> so downstream MCP handlers and the
/// telemetry recorder can pick them up without re-validating.
/// </remarks>
public static class McpBearerAuth
{
    public const string WorkspaceIdKey = "WorkspaceId";
    public const string WorkspaceSlugKey = "WorkspaceSlug";
    public const string ApiKeyIdKey = "ApiKeyId";

    public sealed record AuthResult(
        bool Succeeded,
        int StatusCode,
        Guid WorkspaceId,
        Guid? ApiKeyId,
        string? Error)
    {
        public static AuthResult Fail(int statusCode, string error) =>
            new(false, statusCode, Guid.Empty, null, error);
        public static AuthResult Ok(Guid workspaceId, Guid apiKeyId) =>
            new(true, StatusCodes.Status200OK, workspaceId, apiKeyId, null);
    }

    public static async Task<AuthResult> AuthenticateAsync(
        HttpContext httpContext,
        string workspaceSlug,
        ArchMindDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceSlug))
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "missing workspace slug");
        }

        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var headerValues))
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "missing Authorization header");
        }

        var header = headerValues.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return AuthResult.Fail(
                StatusCodes.Status401Unauthorized,
                "Authorization must use the Bearer scheme");
        }

        var token = header.Substring(scheme.Length).Trim();
        if (string.IsNullOrEmpty(token))
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "empty bearer token");
        }

        // Resolve the workspace from the slug first; this lookup is cheap and lets us
        // return 404-ish (still 401) without hashing the token.
        var workspace = await db.Workspaces
            .AsNoTracking()
            .Where(w => w.Slug == workspaceSlug)
            .Select(w => new { w.Id })
            .FirstOrDefaultAsync(ct);

        if (workspace is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "workspace not found");
        }

        // Validate against workspace_api_keys (SHA-256 hash + non-revoked check is in the service).
        var apiKeyService = httpContext.RequestServices.GetRequiredService<IApiKeyService>();
        var apiKey = await apiKeyService.ValidateAsync(token, ct);
        if (apiKey is null)
        {
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "invalid or revoked api key");
        }

        if (apiKey.WorkspaceId != workspace.Id)
        {
            // The key is valid but belongs to a different workspace. Treat as a 403 so the
            // client can distinguish "wrong workspace" from "bad token".
            return AuthResult.Fail(
                StatusCodes.Status403Forbidden,
                "api key does not belong to this workspace");
        }

        httpContext.Items[WorkspaceIdKey] = workspace.Id;
        httpContext.Items[WorkspaceSlugKey] = workspaceSlug;
        httpContext.Items[ApiKeyIdKey] = apiKey.Id;

        return AuthResult.Ok(workspace.Id, apiKey.Id);
    }
}
