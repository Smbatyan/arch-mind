using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Extraction;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ConflictRow = ArchMind.Core.Entities.CorrelationConflict;
using FileExtractionRow = ArchMind.Core.Entities.FileExtraction;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// BE-026: cross-file correlation job. Runs after per-file
/// <see cref="LlmExtractionJob"/> has populated <c>file_extractions</c> rows
/// for a (workspace, repo). Reads every aggregated <c>FileExtractionRecord</c>,
/// constructs a compact repo-level summary, asks Sonnet to merge / de-duplicate
/// the claims into a canonical topology, then projects the result back onto the
/// AGE graph and persists ambiguities to <c>correlation_conflicts</c>.
///
/// Routes through <see cref="LlmTaskType.CrossFileCorrelation"/> (Sonnet tier).
/// Cached by SHA-256 of the summary JSON via
/// <see cref="ILlmExtractionCacheService"/>.
///
/// Retry: 2 attempts at 60s / 300s. The job is idempotent on the deterministic
/// Guid scheme below.
/// </summary>
[AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 60, 300 })]
public sealed class CrossFileCorrelationJob
{
    private const string ModelId = "sonnet";
    private const string PromptVersion = "2026-05-25/v1";
    private const string ToolName = "emit_correlation_result";
    private const string ToolDescription = "Emit the merged cross-file correlation result.";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        WriteIndented = false,
    };

    private const string SystemPrompt = """
        You are correlating microservice claims across an entire repository.
        Each input describes service-level entities that individual files mentioned.
        Produce:
          - Canonical service list (de-duped, with merged purposes)
          - Event topology: which service publishes/consumes which events.
          - Cross-cutting conventions: only those that appear in >= 50% of services.
          - Suggested conflicts: same event name with different consumer/publisher patterns.
        Be conservative. Skip claims when uncertain.
        """;

    private const string OutputJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["services", "events", "conventions", "conflicts"],
          "properties": {
            "services": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "aliasNames", "sourceFiles"],
                "properties": {
                  "name": { "type": "string" },
                  "purpose": { "type": ["string", "null"] },
                  "aliasNames": { "type": "array", "items": { "type": "string" } },
                  "sourceFiles": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "events": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["eventName", "publishers", "consumers", "topics"],
                "properties": {
                  "eventName": { "type": "string" },
                  "publishers": { "type": "array", "items": { "type": "string" } },
                  "consumers": { "type": "array", "items": { "type": "string" } },
                  "topics": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "conventions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["category", "name", "description", "servicesFollowing"],
                "properties": {
                  "category": { "type": "string" },
                  "name": { "type": "string" },
                  "description": { "type": "string" },
                  "servicesFollowing": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "conflicts": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "description", "involvedServices"],
                "properties": {
                  "kind": { "type": "string" },
                  "description": { "type": "string" },
                  "involvedServices": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          }
        }
        """;

    private readonly ILlmRouter _router;
    private readonly IGraphWriter _graphWriter;
    private readonly IGraphReader _graphReader;
    private readonly ArchMindDbContext _db;
    private readonly ILlmExtractionCacheService _cache;
    private readonly ILogger<CrossFileCorrelationJob> _logger;

    public CrossFileCorrelationJob(
        ILlmRouter router,
        IGraphWriter graphWriter,
        IGraphReader graphReader,
        ArchMindDbContext db,
        ILlmExtractionCacheService cache,
        ILogger<CrossFileCorrelationJob> logger)
    {
        _router = router;
        _graphWriter = graphWriter;
        _graphReader = graphReader;
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Materializes the per-file extractions for the given
    /// (workspace, repo), correlates them via Sonnet, and applies the result to
    /// the graph + <c>correlation_conflicts</c>.
    /// </summary>
    public async Task RunAsync(Guid workspaceId, Guid repoId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty || repoId == Guid.Empty)
        {
            _logger.LogWarning(
                "CrossFileCorrelationJob skipped: empty workspace or repo id workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        _logger.LogInformation(
            "CrossFileCorrelationJob starting workspace={WorkspaceId} repo={RepoId}",
            workspaceId, repoId);

        // 1. Load per-file extractions.
        var extractions = await _db.FileExtractions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.RepoId == repoId)
            .ToListAsync(ct);

        if (extractions.Count == 0)
        {
            _logger.LogInformation(
                "CrossFileCorrelationJob: no file_extractions to correlate workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        // 2. Build compact summary for the LLM.
        var summary = BuildSummary(extractions);
        var summaryJson = JsonSerializer.Serialize(summary, SummaryJsonOptions);
        var correlationContentHash = ComputeSha256Hex(summaryJson);

        // 3. Cache lookup.
        var cached = await _cache.GetAsync<CorrelationResult>(correlationContentHash, ct);
        CorrelationResult? result = cached;

        if (result is null)
        {
            try
            {
                var llmResult = await _router.RouteStructuredAsync<CorrelationResult>(
                    LlmTaskType.CrossFileCorrelation,
                    SystemPrompt,
                    summaryJson,
                    ToolName,
                    ToolDescription,
                    OutputJsonSchema,
                    ct);

                result = llmResult.Output;
                if (result is not null)
                {
                    await _cache.SetAsync(
                        correlationContentHash,
                        workspaceId,
                        ModelId,
                        PromptVersion,
                        result,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Cross-file correlation LLM call failed workspace={WorkspaceId} repo={RepoId}",
                    workspaceId, repoId);
                throw;
            }
        }
        else
        {
            _logger.LogInformation(
                "Cross-file correlation cache hit workspace={WorkspaceId} repo={RepoId} hash={Hash}",
                workspaceId, repoId, correlationContentHash);
        }

        if (result is null)
        {
            _logger.LogWarning(
                "Cross-file correlation produced no result workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return;
        }

        // 4. Apply the result.
        await ApplyToGraphAsync(workspaceId, repoId, result, ct);
        await PersistConflictsAsync(workspaceId, repoId, result, ct);

        // 5. Telemetry. scan_runs has no correlation_run_at column today
        // (Sprint 4 telemetry expansion); skip per spec.

        _logger.LogInformation(
            "CrossFileCorrelationJob complete workspace={WorkspaceId} repo={RepoId} services={Services} events={Events} conventions={Conventions} conflicts={Conflicts}",
            workspaceId,
            repoId,
            result.Services.Count,
            result.Events.Count,
            result.Conventions.Count,
            result.Conflicts.Count);
    }

    /// <summary>
    /// Project the merged <see cref="CorrelationResult"/> onto the AGE graph
    /// using the same MD5-based deterministic Guid scheme as
    /// <see cref="LlmExtractionJob"/> — re-running the correlator on the same
    /// summary targets the same vertices.
    /// </summary>
    private async Task ApplyToGraphAsync(
        Guid workspaceId,
        Guid repoId,
        CorrelationResult result,
        CancellationToken ct)
    {
        await _graphWriter.ExecuteInTransactionAsync(async session =>
        {
            // ---- Canonical services ----
            foreach (var svc in result.Services)
            {
                if (string.IsNullOrWhiteSpace(svc.Name)) continue;

                var serviceId = DeterministicGuid($"Service|{workspaceId}|{svc.Name}");
                await session.UpsertNodeAsync(new GraphNodeSpec(
                    workspaceId,
                    "Service",
                    serviceId,
                    new Dictionary<string, object?>
                    {
                        ["name"] = svc.Name,
                        ["purpose"] = svc.Purpose,
                        ["alias_names"] = svc.AliasNames,
                        ["source_files"] = svc.SourceFiles,
                        ["repo_id"] = repoId.ToString(),
                        ["workspace_id"] = workspaceId.ToString(),
                    }), ct);
            }

            // ---- Event topology ----
            foreach (var evt in result.Events)
            {
                if (string.IsNullOrWhiteSpace(evt.EventName)) continue;

                // Per-file extraction keys Events by (name|version) — we don't
                // have a version coming back from the correlator, so we mirror
                // that scheme with an empty version segment. Matches the
                // identity used in LlmExtractionJob for unversioned events.
                var eventId = DeterministicGuid($"Event|{workspaceId}|{evt.EventName}|");
                await session.UpsertNodeAsync(new GraphNodeSpec(
                    workspaceId,
                    "Event",
                    eventId,
                    new Dictionary<string, object?>
                    {
                        ["name"] = evt.EventName,
                        ["topics"] = evt.Topics,
                        ["publishers"] = evt.Publishers,
                        ["consumers"] = evt.Consumers,
                        ["workspace_id"] = workspaceId.ToString(),
                    }), ct);

                foreach (var publisher in evt.Publishers)
                {
                    if (string.IsNullOrWhiteSpace(publisher)) continue;
                    var publisherId = DeterministicGuid($"Service|{workspaceId}|{publisher}");
                    await session.UpsertEdgeAsync(new GraphEdgeSpec(
                        workspaceId, "PUBLISHES", publisherId, eventId), ct);
                }

                foreach (var consumer in evt.Consumers)
                {
                    if (string.IsNullOrWhiteSpace(consumer)) continue;
                    var consumerId = DeterministicGuid($"Service|{workspaceId}|{consumer}");
                    await session.UpsertEdgeAsync(new GraphEdgeSpec(
                        workspaceId, "CONSUMES", consumerId, eventId), ct);
                }
            }

            // ---- Cross-cutting conventions ----
            foreach (var conv in result.Conventions)
            {
                if (string.IsNullOrWhiteSpace(conv.Name)) continue;

                var conventionId = DeterministicGuid(
                    $"Convention|{workspaceId}|{conv.Category}|{conv.Name}");
                await session.UpsertNodeAsync(new GraphNodeSpec(
                    workspaceId,
                    "Convention",
                    conventionId,
                    new Dictionary<string, object?>
                    {
                        ["category"] = conv.Category,
                        ["name"] = conv.Name,
                        ["description"] = conv.Description,
                        ["services_following"] = conv.ServicesFollowing,
                        ["workspace_id"] = workspaceId.ToString(),
                    }), ct);

                foreach (var serviceName in conv.ServicesFollowing)
                {
                    if (string.IsNullOrWhiteSpace(serviceName)) continue;
                    var serviceId = DeterministicGuid($"Service|{workspaceId}|{serviceName}");
                    await session.UpsertEdgeAsync(new GraphEdgeSpec(
                        workspaceId, "FOLLOWS", serviceId, conventionId), ct);
                }
            }
        }, ct);
    }

    /// <summary>
    /// Persist any conflicts surfaced by the correlator. We log a warning per
    /// conflict so they're visible in operations dashboards even before the
    /// Sprint 5 clarification engine ships.
    /// </summary>
    private async Task PersistConflictsAsync(
        Guid workspaceId,
        Guid repoId,
        CorrelationResult result,
        CancellationToken ct)
    {
        if (result.Conflicts.Count == 0) return;

        var rows = new List<ConflictRow>(result.Conflicts.Count);
        foreach (var c in result.Conflicts)
        {
            _logger.LogWarning(
                "Cross-file correlation conflict workspace={WorkspaceId} repo={RepoId} kind={Kind} services={Services} description={Description}",
                workspaceId,
                repoId,
                c.Kind,
                string.Join(",", c.InvolvedServices ?? Array.Empty<string>()),
                c.Description);

            rows.Add(new ConflictRow
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                RepoId = repoId,
                Kind = Truncate(c.Kind ?? string.Empty, 50),
                Description = Truncate(c.Description ?? string.Empty, 2000),
                Involved = JsonSerializer.Serialize(
                    c.InvolvedServices ?? (IReadOnlyList<string>)Array.Empty<string>()),
                Status = "open",
                CreatedAt = DateTime.UtcNow,
            });
        }

        _db.CorrelationConflicts.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Build the LLM input from raw <see cref="FileExtraction"/> rows. Caps the
    /// per-entity sample paths to 5 so the prompt stays compact on large repos.
    /// </summary>
    private CorrelationSummary BuildSummary(IReadOnlyList<FileExtractionRow> extractions)
    {
        var serviceFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var eventDirections = new Dictionary<string, EventAccumulator>(StringComparer.Ordinal);
        var storageOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var serviceConventionSnippets = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var row in extractions)
        {
            FileExtractionRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<FileExtractionRecord>(
                    row.ExtractionPayload, PayloadJsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping malformed file_extractions row id={Id} file={FilePath}",
                    row.Id, row.FilePath);
                continue;
            }

            if (record is null) continue;

            // Services
            if (record.Service is { IsPartOfService: true, ServiceName: { Length: > 0 } name })
            {
                if (!serviceFiles.TryGetValue(name, out var paths))
                {
                    paths = new List<string>();
                    serviceFiles[name] = paths;
                }
                paths.Add(row.FilePath);

                if (record.Conventions?.Conventions is { Count: > 0 } convs)
                {
                    if (!serviceConventionSnippets.TryGetValue(name, out var snippets))
                    {
                        snippets = new List<string>();
                        serviceConventionSnippets[name] = snippets;
                    }
                    foreach (var c in convs)
                    {
                        var snippetSource = $"{c.Category}|{c.Name}|{c.Description}";
                        snippets.Add(ComputeSha256Hex(snippetSource));
                    }
                }
            }

            // Events published
            if (record.EventsPublished?.Publishes is { Count: > 0 } published)
            {
                foreach (var evt in published)
                {
                    if (string.IsNullOrWhiteSpace(evt.Name)) continue;
                    if (!eventDirections.TryGetValue(evt.Name, out var acc))
                    {
                        acc = new EventAccumulator();
                        eventDirections[evt.Name] = acc;
                    }
                    acc.AddPublish(row.FilePath);
                }
            }

            // Events consumed
            if (record.EventsConsumed?.Consumes is { Count: > 0 } consumed)
            {
                foreach (var evt in consumed)
                {
                    if (string.IsNullOrWhiteSpace(evt.Name)) continue;
                    if (!eventDirections.TryGetValue(evt.Name, out var acc))
                    {
                        acc = new EventAccumulator();
                        eventDirections[evt.Name] = acc;
                    }
                    acc.AddConsume(row.FilePath);
                }
            }

            // Storages
            if (record.Storage?.Storages is { Count: > 0 } storages)
            {
                foreach (var s in storages)
                {
                    if (string.IsNullOrWhiteSpace(s.Name)) continue;
                    var key = $"{s.Name}|{s.Type}|{s.Access}";
                    if (!storageOwners.TryGetValue(key, out var paths))
                    {
                        paths = new List<string>();
                        storageOwners[key] = paths;
                    }
                    paths.Add(row.FilePath);
                }
            }
        }

        var summaryServices = serviceFiles
            .Select(kv => new SummaryService(
                Name: kv.Key,
                FilePathCount: kv.Value.Count,
                MentionFiles: kv.Value.Take(5).ToList(),
                ConventionSnippetHashes:
                    serviceConventionSnippets.TryGetValue(kv.Key, out var hashes)
                        ? hashes.Distinct().Take(10).ToList()
                        : new List<string>()))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var summaryEvents = eventDirections
            .Select(kv => new SummaryEvent(
                Name: kv.Key,
                DeclaredIn: kv.Value.Directions(),
                FilePaths: kv.Value.Files.Take(5).ToList()))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var summaryStorages = storageOwners
            .Select(kv => new SummaryStorage(
                Key: kv.Key,
                FilePathCount: kv.Value.Count,
                MentionFiles: kv.Value.Take(5).ToList()))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        return new CorrelationSummary(summaryServices, summaryEvents, summaryStorages);
    }

    private sealed class EventAccumulator
    {
        private bool _publish;
        private bool _consume;
        public List<string> Files { get; } = new();

        public void AddPublish(string file)
        {
            _publish = true;
            Files.Add(file);
        }

        public void AddConsume(string file)
        {
            _consume = true;
            Files.Add(file);
        }

        public IReadOnlyList<string> Directions()
        {
            var list = new List<string>(2);
            if (_publish) list.Add("publish");
            if (_consume) list.Add("consume");
            return list;
        }
    }

    /// <summary>Compact JSON-friendly summary fed to Sonnet.</summary>
    private sealed record CorrelationSummary(
        IReadOnlyList<SummaryService> Services,
        IReadOnlyList<SummaryEvent> Events,
        IReadOnlyList<SummaryStorage> Storages);

    private sealed record SummaryService(
        string Name,
        int FilePathCount,
        IReadOnlyList<string> MentionFiles,
        IReadOnlyList<string> ConventionSnippetHashes);

    private sealed record SummaryEvent(
        string Name,
        IReadOnlyList<string> DeclaredIn,
        IReadOnlyList<string> FilePaths);

    private sealed record SummaryStorage(
        string Key,
        int FilePathCount,
        IReadOnlyList<string> MentionFiles);

    /// <summary>
    /// MD5(key) → Guid. Identical to <c>LlmExtractionJob.DeterministicGuid</c>;
    /// duplicated here to avoid leaking it as a public API of the worker.
    /// </summary>
    private static Guid DeterministicGuid(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    private static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value.Substring(0, max);
    }
}
