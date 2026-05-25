using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Infrastructure.Skills;

/// <summary>
/// BE-035: MVP heuristic skill matcher. No embeddings — we score by:
/// <list type="bullet">
///   <item>+3.0 per <c>Triggers[]</c> substring found in the lower-cased query.</item>
///   <item>+1.0 per word (len ≥ 4) in <c>Title</c> found in the query.</item>
///   <item>+0.5 per word (len ≥ 4) in <c>Description</c> found in the query.</item>
/// </list>
/// Only skills with <c>Enabled = true</c> are considered. Skills with a score
/// of zero are dropped. Results are sorted by score descending and capped at
/// <c>maxSkills</c>.
/// </summary>
public sealed class SkillMatcher : ISkillMatcher
{
    private static readonly char[] WordSeparators =
    {
        ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']',
        '{', '}', '"', '\'', '/', '\\', '|', '<', '>', '-', '_',
    };

    private readonly ArchMindDbContext _db;

    public SkillMatcher(ArchMindDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<MatchedSkill>> MatchAsync(
        Guid workspaceId,
        string query,
        int maxSkills,
        CancellationToken ct)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("workspaceId is required.", nameof(workspaceId));
        }
        if (maxSkills <= 0) maxSkills = 3;
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<MatchedSkill>();
        }

        var lowered = query.ToLowerInvariant();

        // Pull enabled skills for this workspace into memory. We expect <100
        // skills per workspace at MVP scale — full table-scan is acceptable.
        var skills = await _db.Skills
            .AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Enabled)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Title,
                s.Description,
                s.Body,
                s.Triggers,
            })
            .ToListAsync(ct);

        if (skills.Count == 0) return Array.Empty<MatchedSkill>();

        var matched = new List<MatchedSkill>(skills.Count);
        foreach (var skill in skills)
        {
            double score = 0.0;

            // Triggers: substring presence anywhere in the query (lower-cased).
            if (skill.Triggers is { Length: > 0 })
            {
                foreach (var trigger in skill.Triggers)
                {
                    if (string.IsNullOrWhiteSpace(trigger)) continue;
                    if (lowered.Contains(trigger.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        score += 3.0;
                    }
                }
            }

            // Title words: split, require len ≥ 4, substring presence.
            score += ScoreWords(skill.Title, lowered, weight: 1.0);
            // Description words: same rules, lower weight.
            score += ScoreWords(skill.Description, lowered, weight: 0.5);

            if (score > 0.0)
            {
                matched.Add(new MatchedSkill(skill.Id, skill.Name, skill.Title, skill.Body, score));
            }
        }

        return matched
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(maxSkills)
            .ToList();
    }

    private static double ScoreWords(string source, string loweredQuery, double weight)
    {
        if (string.IsNullOrWhiteSpace(source)) return 0.0;

        double score = 0.0;
        var tokens = source.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in tokens)
        {
            var word = raw.ToLowerInvariant();
            if (word.Length < 4) continue;
            if (!seen.Add(word)) continue; // count each unique word once
            if (loweredQuery.Contains(word, StringComparison.Ordinal))
            {
                score += weight;
            }
        }
        return score;
    }
}
