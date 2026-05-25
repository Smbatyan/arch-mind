using ArchMind.Core.Entities;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// Clones, updates, and cleans up a repository's working directory on local disk.
/// Implementations shell out to the <c>git</c> CLI. Stateless; safe to register as a singleton.
/// </summary>
public interface IRepoCloneService
{
    /// <summary>
    /// Performs an initial shallow clone of <paramref name="repo"/> into <see cref="Repo.WorkingDirPath"/>.
    /// If a working directory already exists at that path it is deleted first.
    /// The PAT is never written to <c>.git/config</c>.
    /// </summary>
    Task CloneAsync(Repo repo, CancellationToken ct = default);

    /// <summary>
    /// Fetches the latest commit on <see cref="Repo.DefaultBranch"/> and hard-resets the working tree.
    /// Returns the previous and current SHAs and whether they differ.
    /// </summary>
    Task<RepoUpdateResult> UpdateAsync(Repo repo, CancellationToken ct = default);

    /// <summary>
    /// Recursively deletes <see cref="Repo.WorkingDirPath"/> if it exists.
    /// </summary>
    Task CleanupAsync(Repo repo, CancellationToken ct = default);

    /// <summary>
    /// Returns the current HEAD SHA of the working dir (output of <c>git rev-parse HEAD</c>).
    /// </summary>
    Task<string> GetCurrentShaAsync(Repo repo, CancellationToken ct = default);
}

/// <summary>
/// Result of <see cref="IRepoCloneService.UpdateAsync"/>:
/// the SHA before the fetch/reset, the SHA after, and whether they differ.
/// </summary>
public record RepoUpdateResult(string PreviousSha, string CurrentSha, bool Changed);
