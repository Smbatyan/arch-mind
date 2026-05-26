namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-036 (Sprint 5): generates the minimum-viable set of clarifying questions
/// for a block of evidence (extraction snippets, conflicting values, file
/// paths). Implementations route to a small/cheap model (Haiku) via
/// <see cref="ILlmRouter"/> and cache by content hash through
/// <see cref="ILlmExtractionCacheService"/>.
/// <para>
/// On any LLM / network / parse error, implementations MUST log and return an
/// empty list — clarification generation is best-effort and never blocks the
/// pipeline.
/// </para>
/// </summary>
public interface IClarificationQuestionGenerator
{
    Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(
        Guid workspaceId,
        Guid? repoId,
        ClarificationEvidence evidence,
        CancellationToken ct);
}

/// <summary>
/// Input bundle for <see cref="IClarificationQuestionGenerator.GenerateAsync"/>.
/// </summary>
/// <param name="Subject">Free-form subject label, e.g. "OrdersService.dependencies".</param>
/// <param name="EvidenceMarkdown">
/// Markdown bullets — typically <c>file:line + value</c> pairs or "service A
/// says X, service B says Y" lines.
/// </param>
/// <param name="RelatedFilePaths">File paths referenced by the evidence.</param>
/// <param name="RelatedNodeNames">Graph node names referenced by the evidence.</param>
public record ClarificationEvidence(
    string Subject,
    string EvidenceMarkdown,
    IReadOnlyList<string> RelatedFilePaths,
    IReadOnlyList<string> RelatedNodeNames);

/// <summary>
/// One LLM-generated clarifying question. Mapped 1:1 to a
/// <see cref="ArchMind.Core.Entities.Clarification"/> row by the caller.
/// </summary>
public record GeneratedQuestion(
    string Topic,
    string Question,
    IReadOnlyList<string> Choices,
    string Severity,
    IReadOnlyList<string> RelatedFilePaths,
    IReadOnlyList<string> RelatedNodeNames);
