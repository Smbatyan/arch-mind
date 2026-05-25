namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-035: heuristic skill matcher used by the MCP <c>get_relevant_context</c>
/// tool. Implementations score every enabled skill in a workspace against a
/// free-text task description and return the top-N hits.
/// </summary>
public interface ISkillMatcher
{
    Task<IReadOnlyList<MatchedSkill>> MatchAsync(
        Guid workspaceId,
        string query,
        int maxSkills,
        CancellationToken ct);
}

/// <summary>One scored skill row. <see cref="Body"/> is the full markdown.</summary>
public sealed record MatchedSkill(
    Guid Id,
    string Name,
    string Title,
    string Body,
    double Score);
