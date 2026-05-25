namespace ArchMind.Infrastructure.Cloning;

/// <summary>
/// Options for <see cref="RepoCloneService"/>. Bound to the <c>Cloning</c> configuration section.
/// </summary>
public sealed class CloningOptions
{
    /// <summary>
    /// Maximum wall-clock seconds for any single <c>git</c> CLI invocation before it is killed.
    /// Defaults to 10 minutes — clones of large repos can be slow.
    /// </summary>
    public int GitTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Root directory under which workspace/repo working trees are materialised.
    /// Effective path: <c>{WorkingDirRoot}/{workspaceId}/repos/{repoId}/</c>.
    /// </summary>
    public string WorkingDirRoot { get; set; } = "/var/archmind/workspaces";
}
