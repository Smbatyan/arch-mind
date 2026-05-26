namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-040 (Sprint 5): looks up answered clarifications relevant to a specific
/// file extraction so the LLM extraction job can inject ground truth back into
/// the prompt. Workspace-scoped; returns at most a small bounded set ordered
/// by most-recent answer first.
/// </summary>
public interface IAnsweredClarificationLookup
{
    /// <summary>
    /// Return answered clarifications whose <c>RelatedFilePaths</c> contains
    /// <paramref name="filePath"/> OR whose <c>RelatedNodeNames</c> overlaps
    /// <paramref name="nodeNames"/>. Bounded to 25 most-recently-answered.
    /// Returns an empty list when nothing matches.
    /// </summary>
    Task<IReadOnlyList<AnsweredClarification>> GetForFileAsync(
        Guid workspaceId,
        string filePath,
        IReadOnlyList<string> nodeNames,
        CancellationToken ct);
}

/// <summary>
/// Minimal projection used by the LLM extraction prompt-injection path —
/// just the bits a Markdown ground-truth block needs. Intentionally narrower
/// than <c>Clarification</c> so consumers don't accidentally rely on schema
/// fields that aren't part of the BE-040 contract.
/// </summary>
public sealed record AnsweredClarification(
    string Topic,
    string Question,
    string Answer,
    IReadOnlyList<string> RelatedFilePaths);
