using System.Text.RegularExpressions;

namespace ArchMind.Infrastructure.Cloning;

/// <summary>
/// Parses GitHub remote URLs into <c>(owner, name)</c> pairs.
/// Accepts the common HTTPS and SSH forms produced by the GitHub UI and CLI.
/// </summary>
public static class GitHubUrlParser
{
    // HTTPS:  https://github.com/owner/repo(.git)?(/)?
    private static readonly Regex HttpsRegex = new(
        @"^https?://(?:www\.)?github\.com/(?<owner>[^/\s]+)/(?<name>[^/\s]+?)(?:\.git)?/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // SSH:    git@github.com:owner/repo(.git)?
    private static readonly Regex SshRegex = new(
        @"^git@github\.com:(?<owner>[^/\s]+)/(?<name>[^/\s]+?)(?:\.git)?/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses <paramref name="githubUrl"/> and returns the repository owner and name.
    /// Accepts:
    /// <list type="bullet">
    ///   <item><c>https://github.com/owner/repo</c></item>
    ///   <item><c>https://github.com/owner/repo.git</c></item>
    ///   <item><c>git@github.com:owner/repo.git</c></item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">If the URL is null, empty, or not a recognised GitHub URL.</exception>
    public static (string Owner, string Name) Parse(string githubUrl)
    {
        if (string.IsNullOrWhiteSpace(githubUrl))
        {
            throw new ArgumentException("GitHub URL must not be null or empty.", nameof(githubUrl));
        }

        var trimmed = githubUrl.Trim();
        var match = HttpsRegex.Match(trimmed);
        if (!match.Success)
        {
            match = SshRegex.Match(trimmed);
        }

        if (!match.Success)
        {
            throw new ArgumentException($"'{githubUrl}' is not a parseable GitHub URL.", nameof(githubUrl));
        }

        return (match.Groups["owner"].Value, match.Groups["name"].Value);
    }
}
