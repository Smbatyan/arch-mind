using ArchMind.Core.Entities;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// BE-041 (Sprint 5): writes <see cref="Clarification"/> candidates produced
/// by the extraction / correlation pipeline. Dedupes by <c>Fingerprint</c>
/// within a workspace so the same ambiguity isn't re-asked on every scan.
/// </summary>
public interface IClarificationWriter
{
    /// <summary>
    /// Insert <paramref name="candidate"/>, deduping by
    /// <c>(WorkspaceId, Fingerprint)</c>.
    /// <para>
    /// <paramref name="candidate"/>.<c>Fingerprint</c> MUST be set (non-null,
    /// non-empty). If a row with the same fingerprint already exists in this
    /// workspace, this is a no-op regardless of that row's status (open,
    /// answered, or dismissed) — the existing row is returned. Otherwise the
    /// candidate is inserted and returned.
    /// </para>
    /// Returns <c>null</c> only when <c>Fingerprint</c> is missing.
    /// </summary>
    Task<Clarification?> UpsertAsync(Clarification candidate, CancellationToken ct);
}
