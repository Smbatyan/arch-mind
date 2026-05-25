namespace ArchMind.Core.Exceptions;

/// <summary>
/// Thrown when a <c>git</c> CLI invocation exits non-zero and the failure does not match
/// a more specific subtype (auth, network). <see cref="Stderr"/> contains the captured
/// standard error stream of the failing process for diagnostics.
/// </summary>
public class RepoCloneException : Exception
{
    public string Stderr { get; }

    public RepoCloneException(string message, string stderr, Exception? inner = null)
        : base(message, inner)
    {
        Stderr = stderr ?? string.Empty;
    }
}

/// <summary>
/// Thrown when the underlying <c>git</c> invocation failed due to credentials
/// (e.g. stderr contained "Authentication failed" or "could not read Username").
/// </summary>
public sealed class RepoAuthException : RepoCloneException
{
    public RepoAuthException(string message, string stderr = "", Exception? inner = null)
        : base(message, stderr, inner) { }
}

/// <summary>
/// Thrown when the underlying <c>git</c> invocation failed due to network issues
/// (e.g. DNS resolution failure, connection timeout).
/// </summary>
public sealed class RepoNetworkException : RepoCloneException
{
    public RepoNetworkException(string message, string stderr = "", Exception? inner = null)
        : base(message, stderr, inner) { }
}
