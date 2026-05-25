namespace ArchMind.Core.Exceptions;

/// <summary>Thrown when GitHub returns 401 (bad or expired PAT).</summary>
public sealed class GitHubAuthException : Exception
{
    public GitHubAuthException(string message) : base(message) { }
    public GitHubAuthException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when GitHub returns 404 (repo/branch/commit missing or no access).</summary>
public sealed class GitHubNotFoundException : Exception
{
    public GitHubNotFoundException(string message) : base(message) { }
    public GitHubNotFoundException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when GitHub returns 403 with x-ratelimit-remaining: 0.</summary>
public sealed class GitHubRateLimitException : Exception
{
    public DateTimeOffset? ResetAt { get; }

    public GitHubRateLimitException(string message, DateTimeOffset? resetAt) : base(message)
    {
        ResetAt = resetAt;
    }

    public GitHubRateLimitException(string message, DateTimeOffset? resetAt, Exception inner) : base(message, inner)
    {
        ResetAt = resetAt;
    }
}

/// <summary>Generic GitHub client failure (network, 5xx, unexpected status).</summary>
public sealed class GitHubClientException : Exception
{
    public GitHubClientException(string message, Exception? inner = null) : base(message, inner) { }
}
