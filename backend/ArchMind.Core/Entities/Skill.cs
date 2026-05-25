using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// BE-033: a user-authored "skill" — a chunk of markdown context loaded by the
/// MCP <c>get_relevant_context</c> tool when its triggers match the agent's
/// task. Skills are workspace-scoped and uniquely identified within a workspace
/// by their <see cref="Name"/> slug.
///
/// Every edit also appends a row to <c>skill_revisions</c> for audit history;
/// see <see cref="SkillRevision"/>.
/// </summary>
public class Skill : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Unique slug per workspace, e.g. <c>checkout-flow</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display title shown in the admin UI.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>1–3 sentence description used by the matcher for word-overlap scoring.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Markdown body returned verbatim when the skill matches.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Keyword / phrase triggers stored as Postgres <c>text[]</c>.</summary>
    public string[] Triggers { get; set; } = Array.Empty<string>();

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
