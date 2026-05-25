namespace ArchMind.Core.Exceptions;

/// <summary>
/// Thrown when the Graphify subprocess exits with a non-zero status code.
/// Captures stderr for diagnostic logs.
/// </summary>
public sealed class GraphifyExecutionException : Exception
{
    public string Stderr { get; }

    public GraphifyExecutionException(string message, string stderr, Exception? inner = null)
        : base(message, inner)
    {
        Stderr = stderr;
    }
}

/// <summary>
/// Thrown when the Graphify subprocess does not complete within the configured timeout.
/// The process tree is killed before this is raised.
/// </summary>
public sealed class GraphifyTimeoutException : Exception
{
    public int TimeoutSeconds { get; }

    public GraphifyTimeoutException(string message, int timeoutSeconds)
        : base(message)
    {
        TimeoutSeconds = timeoutSeconds;
    }
}

/// <summary>
/// Thrown when Graphify's output file is missing, empty, or cannot be parsed
/// into the expected schema.
/// </summary>
public sealed class GraphifyOutputMalformedException : Exception
{
    public GraphifyOutputMalformedException(string message) : base(message) { }
    public GraphifyOutputMalformedException(string message, Exception inner) : base(message, inner) { }
}
