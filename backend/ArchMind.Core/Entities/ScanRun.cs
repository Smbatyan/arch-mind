using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// Per-scan telemetry row written by orchestrator jobs (BE-019: <c>InitialScanJob</c>,
/// later BE-023/024: diff and manual scans). One row per scan invocation: created
/// in <c>"running"</c> state, then updated with totals on completion or failure.
/// Workspace-scoped; queries must filter by <see cref="WorkspaceId"/>.
/// </summary>
public class ScanRun : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RepoId { get; set; }

    /// <summary>"initial" | "diff" | "manual"</summary>
    public string Kind { get; set; } = "initial";

    /// <summary>"running" | "succeeded" | "failed"</summary>
    public string Status { get; set; } = "running";

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FromSha { get; set; }
    public string? ToSha { get; set; }
    public int FilesScanned { get; set; }
    public int FilesEnqueued { get; set; }
    public int GraphifyNodes { get; set; }
    public int GraphifyEdges { get; set; }
    public long TotalTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
