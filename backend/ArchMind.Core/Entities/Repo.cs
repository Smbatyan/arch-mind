using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// A GitHub repository registered to a workspace for scanning and analysis.
/// Workspace-scoped: all queries must filter by WorkspaceId.
/// </summary>
public class Repo : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Display name shown in UI lists / nav. Auto-derived from GitHubUrl repo segment when not provided on create.</summary>
    public string Name { get; set; } = string.Empty;

    public string GitHubUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public string? LastProcessedSha { get; set; }
    public string WorkingDirPath { get; set; } = string.Empty;

    // TODO: Encrypt with pgcrypto pre-release.
    public string PatToken { get; set; } = string.Empty;

    public string Status { get; set; } = "pending";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
