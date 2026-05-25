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
    InferConventions
}
