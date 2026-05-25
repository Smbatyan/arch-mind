using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// Cache entry for deterministic LLM extraction results, keyed by SHA-256 hash of
/// (file content + prompt version + model id). Workspace-scoped so deleting a
/// workspace removes its cache rows.
/// </summary>
public class LlmExtractionCacheEntry : IWorkspaceScoped
{
    public string ContentHash { get; set; } = string.Empty;
    public Guid WorkspaceId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;

    /// <summary>
    /// Serialized JSON result. Mapped to Postgres jsonb.
    /// </summary>
    public string Result { get; set; } = string.Empty;

    public int HitCount { get; set; }
    public DateTime CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
