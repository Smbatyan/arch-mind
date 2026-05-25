using ArchMind.Core.Exceptions;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// Workspace-agnostic GitHub API wrapper. Credentials (PATs) are supplied per-call so
/// they never leak across workspaces. Implementations should construct a fresh underlying
/// client per request and never persist credentials.
/// </summary>
public interface IGitHubClient
{
    /// <summary>
    /// Validate a PAT by calling the authenticated-user endpoint.
    /// Throws <see cref="Exceptions.GitHubAuthException"/> on 401.
    /// </summary>
    Task ValidatePatAsync(string pat, CancellationToken ct = default);

    /// <summary>
    /// Get the HEAD commit SHA for a branch.
    /// </summary>
    Task<string> GetLatestCommitShaAsync(string owner, string repo, string branch, string pat, CancellationToken ct = default);

    /// <summary>
    /// Diff two commits and return the changed file list.
    /// </summary>
    Task<GitHubCompareResult> CompareCommitsAsync(string owner, string repo, string baseSha, string headSha, string pat, CancellationToken ct = default);

    /// <summary>
    /// List all repos visible to the authenticated PAT (public + private).
    /// </summary>
    Task<IReadOnlyList<GitHubRepoSummary>> GetUserReposAsync(string pat, CancellationToken ct = default);
}

/// <summary>Result of a commit comparison.</summary>
public sealed record GitHubCompareResult(IReadOnlyList<GitHubFileChange> Files, int TotalCount);

/// <summary>
/// One changed file inside a compare result.
/// Status is one of "added", "modified", "removed", "renamed".
/// </summary>
public sealed record GitHubFileChange(
    string Filename,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    string? PreviousFilename);

/// <summary>Summary view of a GitHub repository.</summary>
public sealed record GitHubRepoSummary(
    long Id,
    string Owner,
    string Name,
    string FullName,
    string DefaultBranch,
    bool Private);
