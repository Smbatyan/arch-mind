using ArchMind.Core.Entities;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-038 (Sprint 5): scores a <see cref="Clarification"/> on a 0..100 scale.
/// Pure function — no async, no I/O. The caller (intake service) is
/// responsible for resolving graph degree / file counts and packaging them
/// into <see cref="ClarificationPriorityContext"/>.
/// </summary>
public interface IClarificationPrioritizer
{
    /// <summary>
    /// Compute a 0..100 priority score for the candidate clarification given
    /// the supplied context. Higher = more urgent. See
    /// <c>ClarificationPrioritizer</c> for the scoring matrix.
    /// </summary>
    int Score(Clarification clarification, ClarificationPriorityContext ctx);
}

/// <summary>
/// External signals used by <see cref="IClarificationPrioritizer"/>.
/// </summary>
/// <param name="RelatedFileCount">How many files cite the related paths.</param>
/// <param name="RelatedNodeDegree">Sum of graph-degree across related nodes (0 if unknown).</param>
/// <param name="BlocksOtherClarifications">Whether the same topic appears in other open clarifications.</param>
/// <param name="Severity">"low" | "medium" | "high" — from generator or heuristic.</param>
public record ClarificationPriorityContext(
    int RelatedFileCount,
    int RelatedNodeDegree,
    bool BlocksOtherClarifications,
    string Severity);
