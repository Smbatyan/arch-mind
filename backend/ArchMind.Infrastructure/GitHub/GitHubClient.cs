using ArchMind.Core.Abstractions;
using ArchMind.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Octokit;
using IArchMindGitHubClient = ArchMind.Core.Abstractions.IGitHubClient;
using OctokitClient = Octokit.GitHubClient;

namespace ArchMind.Infrastructure.GitHub;

/// <summary>
/// Octokit-backed <see cref="IGitHubClient"/>. Singleton wrapper that constructs a fresh
/// underlying <see cref="OctokitClient"/> for every call so workspace PATs never leak
/// across requests. Rate-limit headers are logged at Information level after each call.
/// </summary>
public sealed class GitHubClient : IArchMindGitHubClient
{
    private static readonly ProductHeaderValue ProductHeader = new("ArchMind", "0.1.0");

    private readonly ILogger<GitHubClient> _logger;

    public GitHubClient(ILogger<GitHubClient> logger)
    {
        _logger = logger;
    }

    public async Task ValidatePatAsync(string pat, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pat))
        {
            throw new GitHubAuthException("PAT must not be empty.");
        }

        var client = CreateClient(pat);
        try
        {
            _ = await client.User.Current().ConfigureAwait(false);
            LogRateLimit(client, nameof(ValidatePatAsync));
        }
        catch (ApiException ex)
        {
            throw MapException(ex, "Failed to validate GitHub PAT.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GitHubClientException("Unexpected error while validating GitHub PAT.", ex);
        }
    }

    public async Task<string> GetLatestCommitShaAsync(string owner, string repo, string branch, string pat, CancellationToken ct = default)
    {
        var client = CreateClient(pat);
        try
        {
            var b = await client.Repository.Branch.Get(owner, repo, branch).ConfigureAwait(false);
            LogRateLimit(client, nameof(GetLatestCommitShaAsync));
            return b.Commit.Sha;
        }
        catch (ApiException ex)
        {
            throw MapException(ex, $"Failed to get latest commit for {owner}/{repo}@{branch}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GitHubClientException($"Unexpected error while reading branch {owner}/{repo}@{branch}.", ex);
        }
    }

    public async Task<GitHubCompareResult> CompareCommitsAsync(string owner, string repo, string baseSha, string headSha, string pat, CancellationToken ct = default)
    {
        var client = CreateClient(pat);
        try
        {
            var compare = await client.Repository.Commit.Compare(owner, repo, baseSha, headSha).ConfigureAwait(false);
            LogRateLimit(client, nameof(CompareCommitsAsync));

            var files = (compare.Files ?? new List<GitHubCommitFile>())
                .Select(f => new GitHubFileChange(
                    Filename: f.Filename ?? string.Empty,
                    Status: f.Status ?? string.Empty,
                    Additions: f.Additions,
                    Deletions: f.Deletions,
                    Changes: f.Changes,
                    PreviousFilename: f.PreviousFileName))
                .ToList();

            return new GitHubCompareResult(files, files.Count);
        }
        catch (ApiException ex)
        {
            throw MapException(ex, $"Failed to compare {owner}/{repo} {baseSha}...{headSha}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GitHubClientException($"Unexpected error while comparing commits for {owner}/{repo}.", ex);
        }
    }

    public async Task<IReadOnlyList<GitHubRepoSummary>> GetUserReposAsync(string pat, CancellationToken ct = default)
    {
        var client = CreateClient(pat);
        try
        {
            var repos = await client.Repository.GetAllForCurrent(new RepositoryRequest
            {
                Visibility = RepositoryRequestVisibility.All,
            }).ConfigureAwait(false);
            LogRateLimit(client, nameof(GetUserReposAsync));

            return repos.Select(r => new GitHubRepoSummary(
                    Id: r.Id,
                    Owner: r.Owner?.Login ?? string.Empty,
                    Name: r.Name ?? string.Empty,
                    FullName: r.FullName ?? string.Empty,
                    DefaultBranch: r.DefaultBranch ?? string.Empty,
                    Private: r.Private))
                .ToList();
        }
        catch (ApiException ex)
        {
            throw MapException(ex, "Failed to list GitHub repositories for the authenticated user.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GitHubClientException("Unexpected error while listing GitHub repositories.", ex);
        }
    }

    private static OctokitClient CreateClient(string pat)
    {
        var client = new OctokitClient(ProductHeader);
        if (!string.IsNullOrEmpty(pat))
        {
            client.Credentials = new Credentials(pat);
        }
        return client;
    }

    private void LogRateLimit(OctokitClient client, string operation)
    {
        try
        {
            var info = client.GetLastApiInfo();
            var rate = info?.RateLimit;
            if (rate is null)
            {
                return;
            }

            _logger.LogInformation(
                "GitHub {Operation} ok: x-ratelimit-remaining={Remaining} x-ratelimit-reset={Reset:O}",
                operation,
                rate.Remaining,
                rate.Reset);
        }
        catch
        {
            // Telemetry must never break the caller.
        }
    }

    private static Exception MapException(ApiException ex, string message)
    {
        var status = (int)ex.StatusCode;
        return status switch
        {
            401 => new GitHubAuthException(message, ex),
            404 => new GitHubNotFoundException(message, ex),
            403 when IsRateLimited(ex) => new GitHubRateLimitException(message, ExtractRateLimitReset(ex), ex),
            _ => new GitHubClientException(message, ex),
        };
    }

    private static bool IsRateLimited(ApiException ex)
    {
        if (ex is RateLimitExceededException or SecondaryRateLimitExceededException or AbuseException)
        {
            return true;
        }

        if (ex.HttpResponse?.Headers is { } headers
            && headers.TryGetValue("X-RateLimit-Remaining", out var remaining)
            && int.TryParse(remaining, out var r)
            && r == 0)
        {
            return true;
        }

        return false;
    }

    private static DateTimeOffset? ExtractRateLimitReset(ApiException ex)
    {
        if (ex is RateLimitExceededException rl)
        {
            return rl.Reset;
        }

        if (ex.HttpResponse?.Headers is { } headers
            && headers.TryGetValue("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset, out var epoch))
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }
}
