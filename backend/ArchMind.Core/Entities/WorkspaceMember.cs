using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// Join table between Workspace and User with a role string.
/// Workspace-scoped: access is always filtered by WorkspaceId.
/// </summary>
public class WorkspaceMember : IWorkspaceScoped
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";

    public Workspace? Workspace { get; set; }
    public User? User { get; set; }
}
