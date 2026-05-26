using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Clarifications;

/// <summary>
/// BE-038 (Sprint 5): pure-function priority scorer for clarifications.
///
/// Scoring matrix (clamped to 0..100):
/// <list type="bullet">
///   <item><description>Base: 50</description></item>
///   <item><description>Severity: high +20, medium +10, low -10</description></item>
///   <item><description>+1 per related file (cap +20)</description></item>
///   <item><description>+1 per 2 nodes of graph degree (cap +15)</description></item>
///   <item><description>+5 if blocks other open clarifications on the same topic</description></item>
///   <item><description>+10 if Source == CrossFileCorrelation</description></item>
/// </list>
/// </summary>
public sealed class ClarificationPrioritizer : IClarificationPrioritizer
{
    private const int Base = 50;
    private const int SeverityHighDelta = 20;
    private const int SeverityMediumDelta = 10;
    private const int SeverityLowDelta = -10;
    private const int FileCapBonus = 20;
    private const int NodeDegreeCapBonus = 15;
    private const int BlocksOthersBonus = 5;
    private const int CorrelationConflictBonus = 10;

    private readonly ILogger<ClarificationPrioritizer> _logger;

    public ClarificationPrioritizer(ILogger<ClarificationPrioritizer> logger)
    {
        _logger = logger;
    }

    public int Score(Clarification clarification, ClarificationPriorityContext ctx)
    {
        if (clarification is null) return Base;
        if (ctx is null)
        {
            return Clamp(Base);
        }

        var score = Base;

        // Severity.
        score += (ctx.Severity ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "high" => SeverityHighDelta,
            "medium" => SeverityMediumDelta,
            "low" => SeverityLowDelta,
            _ => 0
        };

        // Related files (cap +20).
        if (ctx.RelatedFileCount > 0)
        {
            score += Math.Min(ctx.RelatedFileCount, FileCapBonus);
        }

        // Node graph-degree (cap +15, +1 per 2 degree).
        if (ctx.RelatedNodeDegree > 0)
        {
            var nodeBonus = Math.Min(ctx.RelatedNodeDegree / 2, NodeDegreeCapBonus);
            score += nodeBonus;
        }

        if (ctx.BlocksOtherClarifications)
        {
            score += BlocksOthersBonus;
        }

        if (clarification.Source == ClarificationSource.CrossFileCorrelation)
        {
            score += CorrelationConflictBonus;
        }

        var clamped = Clamp(score);
        _logger.LogDebug(
            "Clarification priority computed topic={Topic} severity={Severity} files={Files} degree={Degree} blocks={Blocks} source={Source} raw={Raw} clamped={Clamped}",
            clarification.Topic,
            ctx.Severity,
            ctx.RelatedFileCount,
            ctx.RelatedNodeDegree,
            ctx.BlocksOtherClarifications,
            clarification.Source,
            score,
            clamped);

        return clamped;
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);
}
