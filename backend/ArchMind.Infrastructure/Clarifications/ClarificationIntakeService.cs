using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Clarifications;

/// <summary>
/// BE-038 (Sprint 5): orchestrates the clarification submission pipeline.
/// <list type="number">
///   <item><description>Resolve external signals from <see cref="IGraphReader"/> (degenerate to 0 on lookup failure).</description></item>
///   <item><description>Detect whether the same topic already has open clarifications.</description></item>
///   <item><description>Score via <see cref="IClarificationPrioritizer"/>.</description></item>
///   <item><description>Dedupe-insert via <see cref="IClarificationWriter"/>.</description></item>
/// </list>
/// Severity is supplied by the caller (e.g. by the LLM question generator);
/// unknown / null values normalize to <c>"medium"</c>.
/// </summary>
public sealed class ClarificationIntakeService : IClarificationIntake
{
    private readonly IClarificationPrioritizer _prioritizer;
    private readonly IClarificationWriter _writer;
    private readonly IGraphReader _graphReader;
    private readonly ArchMindDbContext _db;
    private readonly ILogger<ClarificationIntakeService> _logger;

    public ClarificationIntakeService(
        IClarificationPrioritizer prioritizer,
        IClarificationWriter writer,
        IGraphReader graphReader,
        ArchMindDbContext db,
        ILogger<ClarificationIntakeService> logger)
    {
        _prioritizer = prioritizer;
        _writer = writer;
        _graphReader = graphReader;
        _db = db;
        _logger = logger;
    }

    public async Task<Clarification?> SubmitAsync(Clarification candidate, string severity, CancellationToken ct)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));

        // 1. External signals — degrade to 0 on any failure.
        var fileCount = candidate.RelatedFilePaths?.Length ?? 0;
        var nodeDegree = await TryResolveNodeDegreeAsync(candidate, ct);
        var blocks = await TryResolveBlocksOthersAsync(candidate, ct);
        var normalizedSeverity = NormalizeSeverity(severity);

        var ctx = new ClarificationPriorityContext(
            RelatedFileCount: fileCount,
            RelatedNodeDegree: nodeDegree,
            BlocksOtherClarifications: blocks,
            Severity: normalizedSeverity);

        candidate.Priority = _prioritizer.Score(candidate, ctx);

        return await _writer.UpsertAsync(candidate, ct);
    }

    /// <summary>
    /// Resolves the related-node "degree" by issuing one
    /// <see cref="IGraphReader.SearchNodesByTextAsync"/> token-search per name
    /// and summing the hit counts. Cheap proxy for graph importance — matches
    /// spec for BE-038. Any per-name failure is swallowed (treated as 0).
    /// </summary>
    private async Task<int> TryResolveNodeDegreeAsync(Clarification candidate, CancellationToken ct)
    {
        if (candidate.RelatedNodeNames is not { Length: > 0 } names) return 0;
        if (candidate.WorkspaceId == Guid.Empty) return 0;

        const int SearchLimit = 25;
        var total = 0;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                var hits = await _graphReader.SearchNodesByTextAsync(
                    candidate.WorkspaceId,
                    new[] { name },
                    SearchLimit,
                    ct);
                total += hits?.Count ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Clarification node-degree lookup failed workspace={WorkspaceId} name={Name}; treating as 0",
                    candidate.WorkspaceId,
                    name);
            }
        }
        return total;
    }

    private static string NormalizeSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "medium";
        var lowered = raw.Trim().ToLowerInvariant();
        return lowered switch
        {
            "low" or "medium" or "high" => lowered,
            _ => "medium",
        };
    }

    private async Task<bool> TryResolveBlocksOthersAsync(Clarification candidate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidate.Topic)) return false;
        if (candidate.WorkspaceId == Guid.Empty) return false;

        try
        {
            return await _db.Clarifications
                .AsNoTracking()
                .AnyAsync(
                    c => c.WorkspaceId == candidate.WorkspaceId
                      && c.Topic == candidate.Topic
                      && c.Status == ClarificationStatus.Open
                      && (candidate.Fingerprint == null || c.Fingerprint != candidate.Fingerprint),
                    ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Clarification blocks-others lookup failed workspace={WorkspaceId} topic={Topic}; treating as false",
                candidate.WorkspaceId,
                candidate.Topic);
            return false;
        }
    }

}
