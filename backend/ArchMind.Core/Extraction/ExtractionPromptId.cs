namespace ArchMind.Core.Extraction;

/// <summary>
/// Stable identifiers for the per-file semantic extraction prompts. The
/// <see cref="LlmExtractionJob"/> iterates these to produce the aggregated
/// <see cref="FileExtractionRecord"/> persisted to <c>file_extractions</c>.
/// </summary>
public enum ExtractionPromptId
{
    IdentifyService,
    ExtractHttpEndpoints,
    ExtractEventPublishers,
    ExtractEventConsumers,
    ExtractStorageOwnership,
    InferConventions,

    /// <summary>
    /// Outbound + reciprocal-side integration surface (HTTP client calls,
    /// gRPC client/server impls, AMQP/Kafka publish/consume, shared internal
    /// package imports). Used to wire cross-repo CALLS / PUBLISHES / CONSUMES
    /// edges automatically via deterministic node ids.
    /// </summary>
    ExtractIntegrationContracts,

    /// <summary>
    /// BE-036 (Sprint 5): asks Haiku to generate the minimum-viable set of
    /// clarifying questions from an evidence block (extraction snippets,
    /// conflicting values). Output goes through <c>IClarificationQuestionGenerator</c>.
    /// </summary>
    QuestionGeneration
}
