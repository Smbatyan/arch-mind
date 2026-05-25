namespace ArchMind.Core.Extraction;

/// <summary>
/// BE-026: aggregated output of the cross-file correlation LLM call. Consolidates
/// per-file <c>FileExtractionRecord</c> claims into a coherent service / event /
/// convention topology for the whole repo. Persisted in the LLM extraction cache
/// keyed by SHA-256 of the summary input.
/// </summary>
public sealed record CorrelationResult(
    IReadOnlyList<CanonicalService> Services,
    IReadOnlyList<EventTopologyEntry> Events,
    IReadOnlyList<CrossCuttingConvention> Conventions,
    IReadOnlyList<CorrelationConflict> Conflicts
);

/// <summary>
/// Canonical de-duped service entry. <see cref="AliasNames"/> lists alternate
/// names that the correlator merged into <see cref="Name"/>; <see cref="SourceFiles"/>
/// captures which files contributed evidence.
/// </summary>
public sealed record CanonicalService(
    string Name,
    string? Purpose,
    IReadOnlyList<string> AliasNames,
    IReadOnlyList<string> SourceFiles);

/// <summary>
/// One row in the event topology: a single canonical event name plus the
/// services that publish or consume it.
/// </summary>
public sealed record EventTopologyEntry(
    string EventName,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Consumers,
    IReadOnlyList<string> Topics);

/// <summary>
/// A convention shared by ≥ 50% of services (per the system prompt). Listed
/// services represent those the correlator could attribute the convention to.
/// </summary>
public sealed record CrossCuttingConvention(
    string Category,
    string Name,
    string Description,
    IReadOnlyList<string> ServicesFollowing);

/// <summary>
/// A flagged ambiguity / conflict. The downstream <c>CorrelationConflict</c>
/// entity persists each of these for the Sprint 5 clarification engine.
///
/// Note: this record (in the <c>ArchMind.Core.Extraction</c> namespace) is the
/// LLM-output shape; the equally-named <c>CorrelationConflict</c> entity in
/// <c>ArchMind.Core.Entities</c> is the persisted row.
/// </summary>
public sealed record CorrelationConflict(
    string Kind,
    string Description,
    IReadOnlyList<string> InvolvedServices);
