using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// BE-033: append-only revision history for a <see cref="Skill"/>. A new row is
/// written on initial create (version=1) and on every subsequent update (version
/// incremented monotonically per skill). The body is duplicated rather than
/// diffed so revisions are self-contained for audit.
/// </summary>
public class SkillRevision : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid SkillId { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Monotonic per-skill revision number (starts at 1).</summary>
    public int Version { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string[] Triggers { get; set; } = Array.Empty<string>();
    public bool Enabled { get; set; }

    /// <summary>Caller-supplied note explaining this revision (free-form).</summary>
    public string ChangeNote { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public Skill? Skill { get; set; }
    public Workspace? Workspace { get; set; }
}
