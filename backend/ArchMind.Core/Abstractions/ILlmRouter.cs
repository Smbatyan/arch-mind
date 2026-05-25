namespace ArchMind.Core.Abstractions;

/// <summary>
/// Logical task types used to route LLM calls to the appropriate model tier.
/// </summary>
public enum LlmTaskType
{
    Extraction,
    Classification,
    QuestionGeneration,
    CrossFileCorrelation,
    AmbiguityDetection,
    ImpactAnalysis
}

/// <summary>
/// Routes LLM calls to the correct Anthropic model based on task type.
/// Cheap, deterministic tasks go to Haiku; reasoning-heavy tasks go to Sonnet.
/// </summary>
public interface ILlmRouter
{
    Task<AnthropicCallResult<T>> RouteStructuredAsync<T>(
        LlmTaskType taskType,
        string systemPrompt,
        string userPrompt,
        string toolName,
        string toolDescription,
        string jsonSchema,
        CancellationToken ct = default
    ) where T : class;

    Task<AnthropicCallResult<string>> RouteTextAsync(
        LlmTaskType taskType,
        string systemPrompt,
        string userPrompt,
        int maxTokens = 4096,
        CancellationToken ct = default
    );
}
