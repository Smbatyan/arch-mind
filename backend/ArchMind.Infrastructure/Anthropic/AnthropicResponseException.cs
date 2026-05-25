namespace ArchMind.Infrastructure.Anthropic;

/// <summary>
/// Thrown when the Anthropic API returns a response shape we cannot interpret
/// (e.g., missing tool_use block, malformed usage, non-success after retries).
/// </summary>
public class AnthropicResponseException : Exception
{
    public AnthropicResponseException(string message) : base(message) { }
    public AnthropicResponseException(string message, Exception inner) : base(message, inner) { }
}
