using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// Aggregated per-file LLM extraction output. The payload is the JSON
/// serialization of <c>FileExtractionRecord</c> and is stored as JSONB so the
/// orchestrator (Sprint 3) can query/index it before populating the AGE graph.
/// Workspace-scoped.
/// </summary>
public class FileExtraction : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RepoId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Serialized <c>FileExtractionRecord</c> JSON. Mapped to Postgres jsonb.
    /// </summary>
    public string ExtractionPayload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
