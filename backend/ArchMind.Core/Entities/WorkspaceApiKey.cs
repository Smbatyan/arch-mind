using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// A bearer token issued to a workspace, used by external MCP clients
/// (Claude Code, Cursor, etc.) to authenticate against the MCP endpoints.
/// The plaintext token is only ever surfaced at creation time; the database
/// stores a SHA-256 hash plus a short display prefix.
/// Workspace-scoped: all queries must filter by WorkspaceId.
/// </summary>
public class WorkspaceApiKey : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Human-friendly label, e.g. "Claude Code on laptop".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest of the plaintext token.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First 8 chars of the plaintext token (including "am_" prefix), shown in UI lists.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
