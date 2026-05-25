using System.Text.Json;
using System.Text.Json.Serialization;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Mcp;

/// <summary>
/// MCP <c>resources/list</c> and <c>resources/read</c> handler (BE-029).
///
/// Resources are workspace-scoped, read-only JSON documents addressed by
/// <c>archmind://workspace/&lt;slug&gt;/&lt;path&gt;</c> URIs. The handler resolves
/// the workspace from the authenticated <c>WorkspaceId</c> in
/// <see cref="HttpContext.Items"/>; the slug embedded in the URI is validated
/// against that workspace to keep the surface deterministic for AI clients.
/// </summary>
public sealed class McpResourcesHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<McpResourcesHandler> _logger;

    public McpResourcesHandler(ILogger<McpResourcesHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the catalog of resources offered by the workspace. The list is
    /// static — three entries today (graph overview, repos, recent scans).
    /// </summary>
    public McpResponse HandleList(McpRequest request, string workspaceSlug)
    {
        var resources = BuildResourceCatalog(workspaceSlug);
        return BuildResponse(request.Id, new { resources });
    }

    /// <summary>
    /// Reads a single resource by URI. Validates the URI shape and the embedded
    /// workspace slug against the authenticated workspace before resolving the
    /// underlying data source.
    /// </summary>
    public async Task<McpResponse> HandleReadAsync(
        McpRequest request,
        Guid workspaceId,
        string workspaceSlug,
        IGraphReader graphReader,
        ArchMindDbContext db,
        CancellationToken ct)
    {
        var uri = TryGetStringParam(request.Params, "uri");
        if (string.IsNullOrWhiteSpace(uri))
        {
            return Error(request.Id, McpErrorCodes.InvalidParams, "missing 'uri' parameter");
        }

        // archmind://workspace/<slug>/<path>
        const string prefix = "archmind://workspace/";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Error(request.Id, McpErrorCodes.InvalidParams, $"resource URI must start with '{prefix}'");
        }

        var remainder = uri.Substring(prefix.Length);
        var slashIdx = remainder.IndexOf('/');
        if (slashIdx <= 0)
        {
            return Error(request.Id, McpErrorCodes.InvalidParams, "resource URI missing workspace slug or path");
        }

        var slugInUri = remainder.Substring(0, slashIdx);
        var path = remainder.Substring(slashIdx + 1).TrimStart('/');

        if (!string.Equals(slugInUri, workspaceSlug, StringComparison.OrdinalIgnoreCase))
        {
            return Error(request.Id, McpErrorCodes.InvalidParams,
                "resource workspace slug does not match the authenticated workspace");
        }

        object payload;
        try
        {
            payload = path switch
            {
                "graph/overview" => await graphReader.GetOverviewAsync(workspaceId, ct),
                "repos" => await ReadReposAsync(workspaceId, db, ct),
                "recent-scans" => await ReadRecentScansAsync(workspaceId, db, ct),
                _ => throw new ResourceNotFoundException(path),
            };
        }
        catch (ResourceNotFoundException ex)
        {
            return Error(request.Id, McpErrorCodes.InvalidParams, $"unknown resource path: {ex.Path}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to read MCP resource. workspace={WorkspaceId} uri={Uri}",
                workspaceId, uri);
            return Error(request.Id, McpErrorCodes.InternalError, "failed to read resource");
        }

        var text = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new
        {
            contents = new[]
            {
                new
                {
                    uri,
                    mimeType = "application/json",
                    text,
                },
            },
        };

        return BuildResponse(request.Id, content);
    }

    private static IReadOnlyList<object> BuildResourceCatalog(string slug)
    {
        var basePrefix = $"archmind://workspace/{slug}";
        return new object[]
        {
            new
            {
                uri = $"{basePrefix}/graph/overview",
                name = "Graph Overview",
                description = "Workspace-wide node and edge counts grouped by label.",
                mimeType = "application/json",
            },
            new
            {
                uri = $"{basePrefix}/repos",
                name = "Repositories",
                description = "Registered repositories in this workspace.",
                mimeType = "application/json",
            },
            new
            {
                uri = $"{basePrefix}/recent-scans",
                name = "Recent Scans",
                description = "Last 10 scan runs across all repositories in the workspace.",
                mimeType = "application/json",
            },
        };
    }

    private static async Task<object> ReadReposAsync(Guid workspaceId, ArchMindDbContext db, CancellationToken ct)
    {
        var rows = await db.Repos
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId)
            .OrderBy(r => r.GitHubUrl)
            .Select(r => new
            {
                id = r.Id,
                githubUrl = r.GitHubUrl,
                defaultBranch = r.DefaultBranch,
                lastProcessedSha = r.LastProcessedSha,
                status = r.Status,
                updatedAt = r.UpdatedAt,
            })
            .ToListAsync(ct);

        return new { repos = rows };
    }

    private static async Task<object> ReadRecentScansAsync(Guid workspaceId, ArchMindDbContext db, CancellationToken ct)
    {
        var rows = await db.ScanRuns
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderByDescending(s => s.StartedAt)
            .Take(10)
            .Select(s => new
            {
                id = s.Id,
                repoId = s.RepoId,
                kind = s.Kind,
                status = s.Status,
                startedAt = s.StartedAt,
                completedAt = s.CompletedAt,
                fromSha = s.FromSha,
                toSha = s.ToSha,
                filesScanned = s.FilesScanned,
                filesEnqueued = s.FilesEnqueued,
                graphifyNodes = s.GraphifyNodes,
                graphifyEdges = s.GraphifyEdges,
                totalTokens = s.TotalTokens,
                totalCostUsd = s.TotalCostUsd,
                errorMessage = s.ErrorMessage,
            })
            .ToListAsync(ct);

        return new { scans = rows };
    }

    private static string? TryGetStringParam(JsonElement? root, string name)
    {
        if (root is null || root.Value.ValueKind != JsonValueKind.Object) return null;
        return root.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static McpResponse BuildResponse(JsonElement? id, object result) =>
        new(JsonRpc: "2.0", Id: id, Result: result, Error: null);

    private static McpResponse Error(JsonElement? id, int code, string message) =>
        new(JsonRpc: "2.0", Id: id, Result: null, Error: new McpError(code, message, null));

    private sealed class ResourceNotFoundException : Exception
    {
        public string Path { get; }
        public ResourceNotFoundException(string path) : base($"unknown resource: {path}")
        {
            Path = path;
        }
    }
}
