namespace ArchMind.Core.Abstractions;

/// <summary>
/// Logical Anthropic model selector. Concrete model IDs live in the infrastructure
/// pricing/config layer so we can swap underlying revisions without touching callers.
/// </summary>
public enum AnthropicModel
{
    Haiku,
    Sonnet
}

/// <summary>
/// Result of a single Anthropic API call, carrying telemetry (token counts, cost,
/// duration) alongside the deserialized output so callers can log and bill uniformly.
/// </summary>
public record AnthropicCallResult<T>(
    T Output,
    string ModelId,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    TimeSpan Duration
);

/// <summary>
/// Thin abstraction over the Anthropic Messages API. Supports both forced
/// tool-use (for structured JSON output) and plain text completion.
/// </summary>
public interface IAnthropicClient
{
    /// <summary>
    /// Force the model to emit structured JSON by declaring a single tool and
    /// constraining tool_choice to it. Returns the deserialized tool input.
    /// </summary>
    Task<AnthropicCallResult<T>> CompleteStructuredAsync<T>(
        string systemPrompt,
        string userPrompt,
        AnthropicModel model,
        string toolName,
        string toolDescription,
        string jsonSchema,
        CancellationToken ct = default
    ) where T : class;

    /// <summary>
    /// Plain text completion (no tool use).
    /// </summary>
    Task<AnthropicCallResult<string>> CompleteTextAsync(
        string systemPrompt,
        string userPrompt,
        AnthropicModel model,
        int maxTokens = 4096,
        CancellationToken ct = default
    );
}
