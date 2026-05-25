using ArchMind.Core.Abstractions;

namespace ArchMind.Core.Entities;

/// <summary>
/// One record per outbound Anthropic API call, written by the LLM router and
/// related call sites. Used for cost reporting and cache-hit dashboards.
/// Workspace-scoped.
/// </summary>
public class LlmCallLog : IWorkspaceScoped
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>e.g. extraction prompt id name, "Clarification", etc.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>e.g. "claude-haiku-4-5-20251001".</summary>
    public string Model { get; set; } = string.Empty;

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }

    /// <summary>Cost in USD, stored as numeric(12,6).</summary>
    public decimal CostUsd { get; set; }

    public int LatencyMs { get; set; }
    public bool CacheHit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
