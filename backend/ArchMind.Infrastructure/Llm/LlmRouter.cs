using System.Diagnostics;
using ArchMind.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Llm;

/// <summary>
/// Routes LLM calls to Haiku or Sonnet based on task type, per the routing rules in
/// ArchMind-MVP-Technical.md section 7. Caching is handled separately by
/// <see cref="ILlmExtractionCacheService"/>; this router is pure dispatch + logging.
/// </summary>
public class LlmRouter : ILlmRouter
{
    private readonly IAnthropicClient _client;
    private readonly ILogger<LlmRouter> _logger;

    public LlmRouter(IAnthropicClient client, ILogger<LlmRouter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AnthropicCallResult<T>> RouteStructuredAsync<T>(
        LlmTaskType taskType,
        string systemPrompt,
        string userPrompt,
        string toolName,
        string toolDescription,
        string jsonSchema,
        CancellationToken ct = default
    ) where T : class
    {
        var model = SelectModel(taskType);
        var sw = Stopwatch.StartNew();
        var result = await _client.CompleteStructuredAsync<T>(
            systemPrompt, userPrompt, model, toolName, toolDescription, jsonSchema, ct);
        sw.Stop();

        LogCall(taskType, model, result.ModelId, result.InputTokens, result.OutputTokens,
            result.CostUsd, result.Duration, structured: true);

        return result;
    }

    public async Task<AnthropicCallResult<string>> RouteTextAsync(
        LlmTaskType taskType,
        string systemPrompt,
        string userPrompt,
        int maxTokens = 4096,
        CancellationToken ct = default
    )
    {
        var model = SelectModel(taskType);
        var result = await _client.CompleteTextAsync(systemPrompt, userPrompt, model, maxTokens, ct);

        LogCall(taskType, model, result.ModelId, result.InputTokens, result.OutputTokens,
            result.CostUsd, result.Duration, structured: false);

        return result;
    }

    /// <summary>
    /// Routing table from ArchMind-MVP-Technical.md section 7.
    /// Cheap deterministic tasks → Haiku. Reasoning-heavy tasks → Sonnet.
    ///
    /// Cost-optimised mode (current): every task routes to Haiku 4.5. Reasoning
    /// tasks (cross-file correlation, ambiguity detection, impact analysis) take
    /// a quality hit vs Sonnet but stay functional. Flip the Sonnet branches
    /// back on when quality matters more than cost.
    /// </summary>
    private static AnthropicModel SelectModel(LlmTaskType taskType) => taskType switch
    {
        LlmTaskType.Extraction => AnthropicModel.Haiku,
        LlmTaskType.Classification => AnthropicModel.Haiku,
        LlmTaskType.QuestionGeneration => AnthropicModel.Haiku,
        LlmTaskType.CrossFileCorrelation => AnthropicModel.Haiku,
        LlmTaskType.AmbiguityDetection => AnthropicModel.Haiku,
        LlmTaskType.ImpactAnalysis => AnthropicModel.Haiku,
        _ => throw new ArgumentOutOfRangeException(nameof(taskType))
    };

    private void LogCall(
        LlmTaskType taskType,
        AnthropicModel model,
        string modelId,
        int inputTokens,
        int outputTokens,
        decimal costUsd,
        TimeSpan duration,
        bool structured)
    {
        _logger.LogInformation(
            "LLM call routed: task={TaskType} model={Model} modelId={ModelId} structured={Structured} " +
            "inputTokens={InputTokens} outputTokens={OutputTokens} costUsd={CostUsd:F6} durationMs={DurationMs}",
            taskType, model, modelId, structured, inputTokens, outputTokens, costUsd, duration.TotalMilliseconds);
    }
}
