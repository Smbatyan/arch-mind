using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Extraction;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// Workspace-scoped correlation that runs after each repo's
/// <see cref="CrossFileCorrelationJob"/> settles. Reads service/endpoint/event
/// summaries from every repo in the workspace and asks Sonnet to identify
/// cross-repo connections that no single-repo pass can see:
///
/// <list type="bullet">
///   <item>CALLS edges — Service A in repo-1 calls Service B in repo-2
///     (inferred by matching exposed endpoints against service purposes)</item>
///   <item>Cross-repo event flows — confirmed when a published event in
///     repo-1 matches a consumed event name in repo-2 (these already land
///     on the same workspace-scoped Event node via deterministic GUIDs, but
///     this job surfaces them explicitly for the conflict / clarification
///     engine)</item>
/// </list>
///
/// Idempotent: skips when fewer than two repos have file extractions.
/// Cached by SHA-256 of the workspace summary so re-runs after a scan are
/// fast when nothing changed.
///
/// Triggered by <see cref="CrossFileCorrelationJob"/> with a short settle
/// delay so all per-repo correlations have had time to write their nodes
/// before this job reads them.
/// </summary>
[AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 60, 300 })]
public sealed class CrossRepoCorrelationJob
{
    private const string ModelId = "sonnet";
    private const string PromptVersion = "2026-05-26/v1";
    private const string ToolName = "emit_cross_repo_result";
    private const string ToolDescription = "Emit discovered cross-repository service connections.";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        WriteIndented = false,
    };

    private const string SystemPrompt = """
        You are analyzing services that live in separate repositories within the same product workspace.
        Each service summary includes the repository it belongs to, its exposed HTTP endpoints, and
        the events it publishes or consumes.

        Your task: identify which services CALL each other across repository boundaries.

        Rules:
        - A CALLS relationship means Service A sends HTTP requests to Service B (not just
          that they share an event bus).
        - Infer CALLS from endpoint path patterns and service purpose descriptions. For example,
          if ServiceA exposes "POST /api/payments" and ServiceB's purpose mentions "initiates
          payment", ServiceB likely CALLS ServiceA.
        - Only emit CALLS when confidence is high (clear endpoint + purpose alignment).
        - Do NOT emit CALLS between services in the same repository — those are already handled.
        - If a published event in one repo has no consumer in any other repo (or vice versa),
          note it as a cross_repo_event with missing_side = true so it can be surfaced as a gap.
        - Be conservative. Omit uncertain connections.
        """;

    private const string OutputJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["calls", "cross_repo_events"],
          "properties": {
            "calls": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["caller_service", "callee_service", "evidence"],
                "properties": {
                  "caller_service": { "type": "string" },
                  "callee_service": { "type": "string" },
                  "evidence": { "type": "string" }
                }
              }
            },
            "cross_repo_events": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["event_name", "publisher_services", "consumer_services", "missing_side"],
                "properties": {
                  "event_name": { "type": "string" },
                  "publisher_services": { "type": "array", "items": { "type": "string" } },
                  "consumer_services": { "type": "array", "items": { "type": "string" } },
                  "missing_side": { "type": "boolean" }
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
    private readonly IRepoManifestService _manifestService;
    private readonly ILogger<CrossRepoCorrelationJob> _logger;

    public CrossRepoCorrelationJob(
        ILlmRouter router,
        IGraphWriter graphWriter,
        IGraphReader graphReader,
        ArchMindDbContext db,
        ILlmExtractionCacheService cache,
        IRepoManifestService manifestService,
        ILogger<CrossRepoCorrelationJob> logger)
    {
        _router = router;
        _graphWriter = graphWriter;
        _graphReader = graphReader;
        _db = db;
        _cache = cache;
        _manifestService = manifestService;
        _logger = logger;
    }

    public async Task RunAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
        {
            _logger.LogWarning("CrossRepoCorrelationJob skipped: empty workspaceId");
            return;
        }

        _logger.LogInformation("CrossRepoCorrelationJob starting workspace={WorkspaceId}", workspaceId);

        // Load all repos in workspace that have extractions.
        var repoIds = await _db.FileExtractions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .Select(x => x.RepoId)
            .Distinct()
            .ToListAsync(ct);

        if (repoIds.Count < 2)
        {
            _logger.LogInformation(
                "CrossRepoCorrelationJob skipped: fewer than 2 repos with extractions workspace={WorkspaceId} repos={Count}",
                workspaceId, repoIds.Count);
            return;
        }

        // Load repo metadata for display names.
        var repos = await _db.Repos
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && repoIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.GitHubUrl, ct);

        // Deterministic step: shared-package edges via manifest scan.
        // Runs BEFORE the LLM-based correlator so even repos with no service
        // identity (e.g. mobile clients) get connected by their dependency graph.
        await EmitSharedPackageEdgesAsync(workspaceId, repoIds, ct);

        // Load per-repo extractions.
        var allExtractions = await _db.FileExtractions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && repoIds.Contains(x.RepoId))
            .ToListAsync(ct);

        var summary = BuildWorkspaceSummary(allExtractions, repos);
        if (summary.Repos.Count < 2 || summary.Repos.All(r => r.Services.Count == 0))
        {
            _logger.LogInformation(
                "CrossRepoCorrelationJob: LLM correlation skipped (insufficient service data), shared-package edges already emitted; workspace={WorkspaceId}",
                workspaceId);
            return;
        }

        var summaryJson = JsonSerializer.Serialize(summary, SummaryJsonOptions);
        var hash = ComputeSha256Hex(summaryJson);

        var cached = await _cache.GetAsync<CrossRepoResult>(hash, ct);
        CrossRepoResult? result = cached;

        if (result is null)
        {
            try
            {
                var llmResult = await _router.RouteStructuredAsync<CrossRepoResult>(
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
                    await _cache.SetAsync(hash, workspaceId, ModelId, PromptVersion, result, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "CrossRepoCorrelationJob LLM call failed workspace={WorkspaceId}",
                    workspaceId);
                throw;
            }
        }
        else
        {
            _logger.LogInformation(
                "CrossRepoCorrelationJob cache hit workspace={WorkspaceId} hash={Hash}",
                workspaceId, hash);
        }

        if (result is null)
        {
            _logger.LogWarning(
                "CrossRepoCorrelationJob produced no result workspace={WorkspaceId}", workspaceId);
            return;
        }

        var calls = result.Calls ?? Array.Empty<CrossRepoCall>();
        var events = result.CrossRepoEvents ?? Array.Empty<CrossRepoEvent>();
        await ApplyToGraphAsync(workspaceId, calls, ct);

        _logger.LogInformation(
            "CrossRepoCorrelationJob complete workspace={WorkspaceId} calls={Calls} crossEvents={Events}",
            workspaceId, calls.Count, events.Count);
    }

    private async Task EmitSharedPackageEdgesAsync(
        Guid workspaceId, IReadOnlyList<Guid> repoIds, CancellationToken ct)
    {
        // 1. Load manifest per repo.
        var manifests = new Dictionary<Guid, RepoManifest>(repoIds.Count);
        foreach (var repoId in repoIds)
        {
            manifests[repoId] = await _manifestService.ReadAsync(workspaceId, repoId, ct);
        }

        // 2. Load Service nodes per repo from the live graph.
        var allServices = await _graphReader.ListServicesAsync(workspaceId, ct);
        var servicesByRepo = allServices
            .Where(s => s.RepoId is not null)
            .GroupBy(s => s.RepoId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 3. For each ordered repo pair, find shared internal packages, then
        // shared "any" packages as a weaker signal. Emit one edge per service-pair
        // in distinct repos that shared at least one package; carry the package
        // name list + signal-strength property for debuggability.
        var edgesEmitted = 0;
        await _graphWriter.ExecuteInTransactionAsync(async session =>
        {
            for (var i = 0; i < repoIds.Count; i++)
            for (var j = i + 1; j < repoIds.Count; j++)
            {
                var repoA = repoIds[i];
                var repoB = repoIds[j];
                if (!manifests.TryGetValue(repoA, out var mA)
                    || !manifests.TryGetValue(repoB, out var mB)) continue;

                var sharedInternal = mA.InternalPackages
                    .Intersect(mB.InternalPackages, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var sharedAny = sharedInternal.Count == 0
                    ? mA.Packages
                        .Intersect(mB.Packages, StringComparer.OrdinalIgnoreCase)
                        .Take(20)
                        .ToList()
                    : new List<string>();
                if (sharedInternal.Count == 0 && sharedAny.Count == 0) continue;

                if (!servicesByRepo.TryGetValue(repoA, out var svcsA)
                    || !servicesByRepo.TryGetValue(repoB, out var svcsB)) continue;
                if (svcsA.Count == 0 || svcsB.Count == 0) continue;

                var strength = sharedInternal.Count > 0 ? "internal" : "transitive";
                var packages = sharedInternal.Count > 0 ? sharedInternal : sharedAny;

                foreach (var a in svcsA)
                foreach (var b in svcsB)
                {
                    await session.UpsertEdgeAsync(new GraphEdgeSpec(
                        workspaceId,
                        "SHARES_PACKAGE_WITH",
                        a.Id,
                        b.Id,
                        new Dictionary<string, object?>
                        {
                            ["shared_packages"] = packages,
                            ["signal"] = strength,
                        }), ct);
                    edgesEmitted++;
                }
            }
        }, ct);

        _logger.LogInformation(
            "CrossRepoCorrelationJob SHARES_PACKAGE_WITH emitted workspace={WorkspaceId} edges={Edges} repos={Repos}",
            workspaceId, edgesEmitted, repoIds.Count);
    }

    private async Task ApplyToGraphAsync(Guid workspaceId, IReadOnlyList<CrossRepoCall> calls, CancellationToken ct)
    {
        if (calls.Count == 0) return;
        await _graphWriter.ExecuteInTransactionAsync(async session =>
        {
            foreach (var call in calls)
            {
                if (string.IsNullOrWhiteSpace(call.CallerService)
                    || string.IsNullOrWhiteSpace(call.CalleeService))
                    continue;

                var callerId = DeterministicGuid($"Service|{workspaceId}|{call.CallerService}");
                var calleeId = DeterministicGuid($"Service|{workspaceId}|{call.CalleeService}");

                await session.UpsertEdgeAsync(new GraphEdgeSpec(
                    workspaceId,
                    "CALLS",
                    callerId,
                    calleeId,
                    new Dictionary<string, object?>
                    {
                        ["evidence"] = call.Evidence,
                        ["inferred_by"] = "cross_repo_correlation",
                    }), ct);
            }
        }, ct);
    }

    private static WorkspaceSummary BuildWorkspaceSummary(
        IReadOnlyList<ArchMind.Core.Entities.FileExtraction> extractions,
        IReadOnlyDictionary<Guid, string> repoUrls)
    {
        var byRepo = extractions
            .GroupBy(x => x.RepoId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var repoSummaries = new List<RepoSummary>();

        foreach (var (repoId, rows) in byRepo)
        {
            var services = new Dictionary<string, ServiceSummary>(StringComparer.Ordinal);
            var publishedEvents = new HashSet<string>(StringComparer.Ordinal);
            var consumedEvents = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                FileExtractionRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<FileExtractionRecord>(
                        row.ExtractionPayload, PayloadJsonOptions);
                }
                catch
                {
                    continue;
                }

                if (record is null) continue;

                if (record.Service is { IsPartOfService: true, ServiceName: { Length: > 0 } name })
                {
                    if (!services.TryGetValue(name, out var svc))
                    {
                        svc = new ServiceSummary(name, record.Service.ServicePurpose ?? string.Empty,
                            new List<string>());
                        services[name] = svc;
                    }

                    if (record.Endpoints?.Endpoints is { Count: > 0 } eps)
                    {
                        foreach (var ep in eps)
                        {
                            if (!string.IsNullOrWhiteSpace(ep.Method) && !string.IsNullOrWhiteSpace(ep.Path))
                                svc.Endpoints.Add($"{ep.Method} {ep.Path}");
                        }
                    }
                }

                if (record.EventsPublished?.Publishes is { Count: > 0 } pubs)
                    foreach (var e in pubs)
                        if (!string.IsNullOrWhiteSpace(e.Name))
                            publishedEvents.Add(e.Name);

                if (record.EventsConsumed?.Consumes is { Count: > 0 } cons)
                    foreach (var e in cons)
                        if (!string.IsNullOrWhiteSpace(e.Name))
                            consumedEvents.Add(e.Name);
            }

            var repoLabel = repoUrls.TryGetValue(repoId, out var url)
                ? url.Split('/').TakeLast(1).FirstOrDefault() ?? repoId.ToString()
                : repoId.ToString();

            repoSummaries.Add(new RepoSummary(
                repoLabel,
                services.Values.Select(s => new ServiceEntry(
                    s.Name,
                    s.Purpose,
                    s.Endpoints.Distinct().Take(20).ToList())).ToList(),
                publishedEvents.ToList(),
                consumedEvents.ToList()));
        }

        return new WorkspaceSummary(repoSummaries);
    }

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

    // ---- local DTOs ----

    private sealed record WorkspaceSummary(IReadOnlyList<RepoSummary> Repos);

    private sealed record RepoSummary(
        string RepoLabel,
        IReadOnlyList<ServiceEntry> Services,
        IReadOnlyList<string> PublishedEvents,
        IReadOnlyList<string> ConsumedEvents);

    private sealed record ServiceEntry(
        string Name,
        string Purpose,
        IReadOnlyList<string> Endpoints);

    private sealed class ServiceSummary
    {
        public string Name { get; }
        public string Purpose { get; }
        public List<string> Endpoints { get; }

        public ServiceSummary(string name, string purpose, List<string> endpoints)
        {
            Name = name;
            Purpose = purpose;
            Endpoints = endpoints;
        }
    }

    private sealed record CrossRepoResult(
        IReadOnlyList<CrossRepoCall> Calls,
        IReadOnlyList<CrossRepoEvent> CrossRepoEvents);

    private sealed record CrossRepoCall(
        string CallerService,
        string CalleeService,
        string Evidence);

    private sealed record CrossRepoEvent(
        string EventName,
        IReadOnlyList<string> PublisherServices,
        IReadOnlyList<string> ConsumerServices,
        bool MissingSide);
}
