namespace ArchMind.Workers.Polling;

/// <summary>
/// Manages the per-repo Hangfire recurring poll jobs (BE-024). Implementations
/// wrap Hangfire's recurring-job manager so the application code that creates
/// or deletes a repo doesn't need to know about Hangfire job ids or cron.
/// </summary>
public interface IPollingRegistrar
{
    /// <summary>
    /// Add (or update) the recurring poll job for the given repo. Idempotent;
    /// safe to call on every startup and on every repo create.
    /// </summary>
    void RegisterRepo(Guid workspaceId, Guid repoId);

    /// <summary>
    /// Remove the recurring poll job for the given repo. Idempotent; safe to
    /// call even if the job was never registered.
    /// </summary>
    void UnregisterRepo(Guid workspaceId, Guid repoId);

    /// <summary>
    /// Load every repo and register a recurring poll job for it. Invoked on
    /// application startup so restarts don't drop existing schedules.
    /// </summary>
    Task SynchronizeAllAsync(CancellationToken ct = default);
}
