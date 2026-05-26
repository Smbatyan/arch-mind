using ArchMind.Core.Entities;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-038 (Sprint 5) orchestration entry point: takes a <see cref="Clarification"/>
/// candidate plus a severity hint, computes its priority via
/// <see cref="IClarificationPrioritizer"/> (with related-file count / node degree
/// resolved from <see cref="IGraphReader"/> when names are supplied), then hands
/// off to <see cref="IClarificationWriter"/> for dedupe-insert.
/// <para>
/// Always returns the writer's verdict — null only when the writer rejects the
/// candidate (e.g. missing fingerprint).
/// </para>
/// </summary>
public interface IClarificationIntake
{
    /// <summary>
    /// Score and dedupe-insert <paramref name="candidate"/>. <paramref name="severity"/>
    /// is expected to be one of <c>"low" | "medium" | "high"</c> (case-insensitive);
    /// unknown values default to <c>"medium"</c> inside the prioritizer.
    /// </summary>
    Task<Clarification?> SubmitAsync(Clarification candidate, string severity, CancellationToken ct);
}
