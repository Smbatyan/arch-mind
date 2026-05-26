using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Clarifications;

/// <summary>
/// BE-041 (Sprint 5): default <see cref="IClarificationWriter"/>. Inserts
/// candidates produced by the extraction / correlation pipeline and dedupes
/// by <c>(WorkspaceId, Fingerprint)</c>. The actual dedupe constraint is a
/// Postgres partial unique index — this method does an existence check first
/// so the common case ("we've already asked this") is a cheap no-op SELECT
/// rather than an INSERT that races onto the index and throws.
/// </summary>
public sealed class ClarificationWriter : IClarificationWriter
{
    private readonly ArchMindDbContext _db;
    private readonly ILogger<ClarificationWriter> _logger;

    public ClarificationWriter(ArchMindDbContext db, ILogger<ClarificationWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Clarification?> UpsertAsync(Clarification candidate, CancellationToken ct)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (string.IsNullOrWhiteSpace(candidate.Fingerprint))
        {
            _logger.LogWarning(
                "ClarificationWriter.UpsertAsync rejected candidate with empty fingerprint workspace={WorkspaceId} topic={Topic}",
                candidate.WorkspaceId,
                candidate.Topic);
            return null;
        }

        var existing = await _db.Clarifications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.WorkspaceId == candidate.WorkspaceId
                  && c.Fingerprint == candidate.Fingerprint,
                ct);

        if (existing is not null)
        {
            // Regardless of Open/Answered/Dismissed, the contract says no-op.
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        if (candidate.Id == Guid.Empty) candidate.Id = Guid.NewGuid();
        if (candidate.CreatedAt == default) candidate.CreatedAt = now;
        candidate.UpdatedAt = now;

        _db.Clarifications.Add(candidate);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Lost a race on the partial unique index — re-fetch and return
            // the winner so callers always see a consistent row.
            _logger.LogInformation(
                ex,
                "Clarification insert race lost workspace={WorkspaceId} fingerprint={Fingerprint}; returning existing row",
                candidate.WorkspaceId,
                candidate.Fingerprint);
            _db.Entry(candidate).State = EntityState.Detached;
            return await _db.Clarifications
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    c => c.WorkspaceId == candidate.WorkspaceId
                      && c.Fingerprint == candidate.Fingerprint,
                    ct);
        }

        return candidate;
    }
}
