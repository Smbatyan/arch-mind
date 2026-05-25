using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// BE-026: a cross-file correlation conflict surfaced by the
/// <c>CrossFileCorrelationJob</c>. Persisted so the Sprint 5 clarification engine
/// can present these to a human reviewer. Workspace-scoped.
///
/// Examples of <see cref="Kind"/>:
/// <list type="bullet">
///   <item><c>duplicate_event</c> — same event name with conflicting publisher/consumer patterns.</item>
///   <item><c>same_storage_owners</c> — two services both claim ownership of the same storage.</item>
///   <item><c>ambiguous_publisher</c> — multiple services claim to publish the same event.</item>
/// </list>
/// </summary>
public class CorrelationConflict : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RepoId { get; set; }

    /// <summary>Short conflict taxonomy slug (max 50 chars).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Human-readable description of the conflict (max 2000 chars).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of service names involved in the conflict.
    /// Stored as Postgres jsonb.
    /// </summary>
    public string Involved { get; set; } = "[]";

    /// <summary>"open" | "resolved" | "deferred". Defaults to "open".</summary>
    public string Status { get; set; } = "open";

    public DateTime CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
