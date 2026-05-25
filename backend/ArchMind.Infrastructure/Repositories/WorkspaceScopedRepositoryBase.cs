using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Data;

namespace ArchMind.Infrastructure.Repositories;

/// <summary>
/// Base class for repositories of workspace-scoped entities.
/// Forces every query through <see cref="Query"/> with an explicit workspace_id filter.
/// </summary>
public abstract class WorkspaceScopedRepositoryBase<T> where T : class, IWorkspaceScoped
{
    protected readonly ArchMindDbContext Db;

    protected WorkspaceScopedRepositoryBase(ArchMindDbContext db)
    {
        Db = db;
    }

    /// <summary>
    /// Returns a queryable filtered by the given workspace id. Always use this.
    /// </summary>
    protected IQueryable<T> Query(Guid workspaceId)
    {
        return Db.Set<T>().Where(x => x.WorkspaceId == workspaceId);
    }

    /// <summary>
    /// Escape hatch that intentionally refuses to run. Subclasses must override
    /// only if they have a documented reason to bypass the tenant filter.
    /// </summary>
    protected virtual IQueryable<T> QueryNoFilter()
    {
        throw new InvalidOperationException("Workspace filter required.");
    }
}
