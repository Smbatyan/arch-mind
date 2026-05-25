namespace ArchMind.Core.Entities;

/// <summary>
/// A workspace is a tenant boundary. Every domain row (other than global users) belongs to a workspace.
/// </summary>
public class Workspace
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = new List<WorkspaceMember>();
}
