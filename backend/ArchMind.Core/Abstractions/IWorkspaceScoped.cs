namespace ArchMind.Core.Abstractions;

/// <summary>
/// Marker interface for any entity that belongs to a workspace (tenant).
/// All repositories querying these entities MUST filter by WorkspaceId.
/// </summary>
public interface IWorkspaceScoped
{
    Guid WorkspaceId { get; }
}
