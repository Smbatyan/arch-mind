using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Core.Extraction;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Clarifications;

/// <summary>
/// BE-036 (Sprint 5): generates clarifying questions for an evidence block via
/// <see cref="ILlmRouter"/> (routes <see cref="LlmTaskType.QuestionGeneration"/>
/// to Haiku per the standard routing table). Caches per-call by SHA-256 of
/// <c>Subject + EvidenceMarkdown</c> through <see cref="ILlmExtractionCacheService"/>
/// keyed on the QuestionGeneration prompt version.
/// <para>
/// LLM / parse / network errors are swallowed (logged) — the caller always
/// gets a non-null list. Clarification generation is best-effort enrichment.
/// </para>
/// </summary>
public sealed class ClarificationQuestionGenerator : IClarificationQuestionGenerator
{
    private const string ModelIdLabel = "haiku"; // mirrors ClarificationQuestionGenerator routing
    private const string Purpose = "QuestionGeneration";

    private readonly ILlmRouter _router;
    private readonly ILlmExtractionCacheService _cache;
    private readonly ITelemetryRecorder _telemetry;
    private readonly IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> _prompts;
    private readonly ILogger<ClarificationQuestionGenerator> _logger;

    public ClarificationQuestionGenerator(
        ILlmRouter router,
        ILlmExtractionCacheService cache,
        ITelemetryRecorder telemetry,
        IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> prompts,
        ILogger<ClarificationQuestionGenerator> logger)
    {
        _router = router;
        _cache = cache;
        _telemetry = telemetry;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(
        Guid workspaceId,
        Guid? repoId,
        ClarificationEvidence evidence,
        CancellationToken ct)
    {
        if (evidence is null) return Array.Empty<GeneratedQuestion>();
        if (string.IsNullOrWhiteSpace(evidence.EvidenceMarkdown))
        {
            return Array.Empty<GeneratedQuestion>();
        }

        if (!_prompts.TryGetValue(ExtractionPromptId.QuestionGeneration, out var prompt))
        {
            _logger.LogWarning(
                "QuestionGeneration prompt missing from library; returning no questions workspace={WorkspaceId}",
                workspaceId);
            return Array.Empty<GeneratedQuestion>();
        }

        var cacheInput = (evidence.EvidenceMarkdown ?? string.Empty) + "␟" + (evidence.Subject ?? string.Empty);
        var contentHash = _cache.ComputeKey(cacheInput, prompt.Version, ModelIdLabel);

        // 1. Cache lookup.
        try
        {
            var cached = await _cache.GetAsync<QuestionGenerationPayload>(contentHash, ct);
            if (cached is { Questions: { Count: > 0 } })
            {
                return Project(cached);
            }
            if (cached is not null)
            {
                // Cached empty result — honour it.
                return Array.Empty<GeneratedQuestion>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "QuestionGeneration cache lookup failed workspace={WorkspaceId} hash={Hash}; falling through to LLM",
                workspaceId,
                contentHash);
        }

        // 2. Build the user prompt by interpolating into the template.
        var userPrompt = prompt.UserPromptTemplate
            .Replace("{subject}", evidence.Subject ?? string.Empty)
            .Replace("{evidence_markdown}", evidence.EvidenceMarkdown ?? string.Empty)
            .Replace("{related_files}", FormatList(evidence.RelatedFilePaths))
            .Replace("{related_nodes}", FormatList(evidence.RelatedNodeNames));

        QuestionGenerationPayload? payload;
        AnthropicCallResult<QuestionGenerationPayload>? llmResult = null;
        try
        {
            llmResult = await _router.RouteStructuredAsync<QuestionGenerationPayload>(
                LlmTaskType.QuestionGeneration,
                prompt.SystemPrompt,
                userPrompt,
                prompt.ToolName,
                prompt.ToolDescription,
                prompt.OutputJsonSchema,
                ct);
            payload = llmResult.Output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "QuestionGeneration LLM call failed workspace={WorkspaceId} subject={Subject}; returning no questions",
                workspaceId,
                evidence.Subject);
            return Array.Empty<GeneratedQuestion>();
        }

        // 3. Telemetry — best-effort.
        if (llmResult is not null)
        {
            try
            {
                await _telemetry.RecordLlmCallAsync(new LlmCallLog
                {
                    WorkspaceId = workspaceId,
                    Purpose = Purpose,
                    Model = llmResult.ModelId,
                    InputTokens = llmResult.InputTokens,
                    OutputTokens = llmResult.OutputTokens,
                    CostUsd = llmResult.CostUsd,
                    LatencyMs = (int)Math.Min(int.MaxValue, llmResult.Duration.TotalMilliseconds),
                    CacheHit = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "QuestionGeneration telemetry record failed workspace={WorkspaceId}",
                    workspaceId);
            }
        }

        // 4. Cache (always — even empty result, so we don't re-pay for the same null answer).
        try
        {
            await _cache.SetAsync(
                contentHash,
                workspaceId,
                llmResult?.ModelId ?? ModelIdLabel,
                prompt.Version,
                payload ?? new QuestionGenerationPayload { Questions = new List<QuestionItem>() },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "QuestionGeneration cache write failed workspace={WorkspaceId} hash={Hash}",
                workspaceId,
                contentHash);
        }

        return payload is null ? Array.Empty<GeneratedQuestion>() : Project(payload);
    }

    private static IReadOnlyList<GeneratedQuestion> Project(QuestionGenerationPayload payload)
    {
        if (payload.Questions is not { Count: > 0 } items) return Array.Empty<GeneratedQuestion>();

        var result = new List<GeneratedQuestion>(items.Count);
        foreach (var q in items)
        {
            if (q is null) continue;
            if (string.IsNullOrWhiteSpace(q.Topic) || string.IsNullOrWhiteSpace(q.Question)) continue;

            result.Add(new GeneratedQuestion(
                Topic: q.Topic.Trim(),
                Question: q.Question.Trim(),
                Choices: q.Choices ?? (IReadOnlyList<string>)Array.Empty<string>(),
                Severity: NormalizeSeverity(q.Severity),
                RelatedFilePaths: q.RelatedFiles ?? (IReadOnlyList<string>)Array.Empty<string>(),
                RelatedNodeNames: q.RelatedNodes ?? (IReadOnlyList<string>)Array.Empty<string>()));
        }
        return result;
    }

    private static string NormalizeSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "medium";
        var lowered = raw.Trim().ToLowerInvariant();
        return lowered switch
        {
            "low" or "medium" or "high" => lowered,
            _ => "medium"
        };
    }

    private static string FormatList(IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0) return "(none)";
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            sb.Append("- ").Append(item).Append('\n');
        }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    // DTOs matching the QuestionGeneration tool schema. Public to satisfy the
    // `class` constraint on IAnthropicClient.CompleteStructuredAsync<T>.
    public sealed class QuestionGenerationPayload
    {
        [JsonPropertyName("questions")]
        public List<QuestionItem> Questions { get; set; } = new();
    }

    public sealed class QuestionItem
    {
        [JsonPropertyName("topic")]
        public string? Topic { get; set; }

        [JsonPropertyName("question")]
        public string? Question { get; set; }

        [JsonPropertyName("choices")]
        public List<string>? Choices { get; set; }

        [JsonPropertyName("severity")]
        public string? Severity { get; set; }

        [JsonPropertyName("related_files")]
        public List<string>? RelatedFiles { get; set; }

        [JsonPropertyName("related_nodes")]
        public List<string>? RelatedNodes { get; set; }
    }
}
