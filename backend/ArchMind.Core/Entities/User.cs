namespace ArchMind.Core.Entities;

/// <summary>
/// Users are global. A user can belong to multiple workspaces via WorkspaceMember.
/// </summary>
public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public ICollection<WorkspaceMember> Memberships { get; set; } = new List<WorkspaceMember>();
}
