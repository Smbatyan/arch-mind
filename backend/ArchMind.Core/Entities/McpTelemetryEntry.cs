using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// One record per inbound MCP request, written by the MCP telemetry middleware
/// (wired in Wave 2). Used to surface per-workspace usage / error dashboards.
/// Workspace-scoped.
/// </summary>
public class McpTelemetryEntry : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>The API key the request authenticated with, if any.</summary>
    public Guid? ApiKeyId { get; set; }

    /// <summary>Logical MCP method, e.g. "tools/call:get_relevant_context".</summary>
    public string Method { get; set; } = string.Empty;

    public int StatusCode { get; set; }
    public int LatencyMs { get; set; }
    public int? RequestSizeBytes { get; set; }
    public int? ResponseSizeBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
