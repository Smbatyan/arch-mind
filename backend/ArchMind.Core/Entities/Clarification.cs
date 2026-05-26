using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// BE-037 (Sprint 5): a "clarification" is an open question surfaced to a
/// human reviewer when ArchMind's extraction pipeline can't decide something
/// on its own — typically either per-file LLM ambiguity or a cross-file
/// correlation conflict. Workspace-scoped; deduped by <see cref="Fingerprint"/>.
/// </summary>
public class Clarification : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Optional repo. Set null on repo delete so history survives.</summary>
    public Guid? RepoId { get; set; }

    public ClarificationSource Source { get; set; }

    /// <summary>Short topic label, e.g. "OrdersService.dependencies".</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>Human-readable question shown to the reviewer.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// Optional markdown context block — cited file paths, snippets, the
    /// conflicting evidence the question is asking the human to resolve.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Optional multiple-choice list. Stored as Postgres text[]; null or empty
    /// means free-form answer. Individual entries can be null for "other".
    /// </summary>
    public string?[] Choices { get; set; } = Array.Empty<string?>();

    /// <summary>
    /// 0..100, higher = more urgent. Default 50; BE-038 (Wave 2) re-scores.
    /// </summary>
    public int Priority { get; set; }

    public ClarificationStatus Status { get; set; } = ClarificationStatus.Open;

    /// <summary>Free-form answer text OR the chosen entry from <see cref="Choices"/>.</summary>
    public string? Answer { get; set; }

    /// <summary>
    /// String for flexibility — may be a user id, email, or external identity.
    /// </summary>
    public string? AnsweredByUserId { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>Files referenced by this clarification (Postgres text[]).</summary>
    public string[] RelatedFilePaths { get; set; } = Array.Empty<string>();

    /// <summary>Graph node names referenced by this clarification (Postgres text[]).</summary>
    public string[] RelatedNodeNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Dedupe key — typically SHA-256 hex of normalized topic + sorted
    /// related-file-paths. The schema enforces (workspace_id, fingerprint)
    /// unique-when-not-null via a partial index.
    /// </summary>
    public string? Fingerprint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ClarificationStatus
{
    Open,
    Answered,
    Dismissed,
}

public enum ClarificationSource
{
    FileExtraction,
    CrossFileCorrelation,
    ManualLlmGen,
}
