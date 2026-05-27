using System.Security.Cryptography;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Extraction;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// Hangfire job that runs the full extraction prompt library against a single
/// file and persists the aggregated <see cref="FileExtractionRecord"/> via
/// <see cref="IFileExtractionRepository"/>.
///
/// Idempotent on (workspaceId, repoId, filePath, contentHash): same inputs hit
/// the cache and upsert the same row.
/// </summary>
[AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
public sealed class LlmExtractionJob
{
    private const string ModelId = "haiku";

    private readonly ILlmRouter _router;
    private readonly ILlmExtractionCacheService _cache;
    private readonly IFileContentResolver _files;
    private readonly IFileExtractionRepository _repository;
    private readonly IGraphWriter _graphWriter;
    private readonly IAnsweredClarificationLookup _clarifications;
    private readonly IGraphifyContextService _graphifyContext;
    private readonly IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> _prompts;
    private readonly ILogger<LlmExtractionJob> _logger;

    public LlmExtractionJob(
        ILlmRouter router,
        ILlmExtractionCacheService cache,
        IFileContentResolver files,
        IFileExtractionRepository repository,
        IGraphWriter graphWriter,
        IAnsweredClarificationLookup clarifications,
        IGraphifyContextService graphifyContext,
        IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> prompts,
        ILogger<LlmExtractionJob> logger)
    {
        _router = router;
        _cache = cache;
        _files = files;
        _repository = repository;
        _graphWriter = graphWriter;
        _clarifications = clarifications;
        _graphifyContext = graphifyContext;
        _prompts = prompts;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Runs all 6 extraction prompts against the file at
    /// <paramref name="filePath"/>, leaning on <see cref="ILlmExtractionCacheService"/>
    /// to avoid redundant LLM calls.
    /// </summary>
    public async Task RunAsync(
        Guid workspaceId,
        Guid repoId,
        string filePath,
        string contentHash,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "LLM extraction starting workspace={WorkspaceId} repo={RepoId} file={FilePath} hash={Hash}",
            workspaceId, repoId, filePath, contentHash);

        var raw = await _files.ReadAsync(workspaceId, repoId, filePath, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogInformation(
                "Skipping extraction: file empty or oversized workspace={WorkspaceId} repo={RepoId} file={FilePath}",
                workspaceId, repoId, filePath);
            return;
        }

        // Trim comments + whitespace to reduce input tokens before LLM dispatch.
        var trimResult = FileContentTrimmer.Trim(filePath, raw);
        var trimmed = trimResult.Content;
        _logger.LogDebug(
            "Trimmed file {FilePath}: originalChars={OriginalChars} trimmedChars={TrimmedChars} estTokens={EstTokens}",
            filePath, raw.Length, trimmed.Length, trimResult.EstimatedTokens);

        // Load graphify structural context (AST nodes/edges) for this file so
        // LLM prompts receive pre-computed call-graph and type information.
        // Returns empty silently when graphify-out/graph.json is absent.
        var graphifyCtx = await _graphifyContext.GetFileContextAsync(workspaceId, repoId, filePath, ct);
        var (structuralBlock, structuralSuffix) = BuildStructuralContext(graphifyCtx);
        if (!graphifyCtx.IsEmpty)
        {
            _logger.LogDebug(
                "Graphify context loaded for {FilePath}: nodes={Nodes} outEdges={OutEdges}",
                filePath, graphifyCtx.Nodes.Count, graphifyCtx.OutboundEdges.Count);
        }

        // BE-040: pre-load answered clarifications keyed by file path. This is
        // the first of two lookups — we do a second one after service ID so
        // clarifications attached to the service name (but no file path) flow
        // into the remaining 5 prompts. Empty result keeps the cache key
        // identical to the pre-BE-040 wire format.
        var fileClarifications = await _clarifications.GetForFileAsync(
            workspaceId, filePath, Array.Empty<string>(), ct);
        var (fileGroundTruth, fileClfSuffix) = BuildGroundTruth(fileClarifications);

        // TODO(cost): collapse the 7 per-file prompts into 1 mega-call that
        // returns all sections (service/endpoints/events/storage/conventions/
        // contracts) in a single Anthropic round-trip. Expected ~5× token
        // reduction by amortising one system prompt over all sections instead
        // of paying for it 7×. Requires a unified ExtractionResult schema and
        // partial-result tolerance (one mega-call failing loses everything,
        // vs. today's per-prompt isolation). Tracked separately; do NOT do
        // this opportunistically — measure quality first because the mega
        // prompt is harder for Haiku to follow.
        var service = await RunPromptAsync<ServiceExtraction>(
            workspaceId, ExtractionPromptId.IdentifyService, filePath, trimmed,
            fileGroundTruth, fileClfSuffix, structuralBlock, structuralSuffix, ct);

        // After service identification, fold the service name into the lookup
        // so service-scoped clarifications enrich the remaining prompts. We
        // re-merge against the file-path lookup so we don't lose answers that
        // were only file-pathed (e.g. "what does this storage adapter do?").
        var nodeNames = !string.IsNullOrWhiteSpace(service?.ServiceName)
            ? new[] { service!.ServiceName! }
            : Array.Empty<string>();

        IReadOnlyList<AnsweredClarification> merged;
        string mergedGroundTruth;
        string mergedClfSuffix;
        if (nodeNames.Length == 0)
        {
            merged = fileClarifications;
            mergedGroundTruth = fileGroundTruth;
            mergedClfSuffix = fileClfSuffix;
        }
        else
        {
            var serviceClarifications = await _clarifications.GetForFileAsync(
                workspaceId, filePath, nodeNames, ct);
            // GetForFileAsync OR's filePath and nodeNames, so the service
            // lookup is a strict superset of the file-only lookup — replace
            // wholesale rather than deduping by hand.
            merged = serviceClarifications;
            (mergedGroundTruth, mergedClfSuffix) = BuildGroundTruth(merged);
        }

        if (merged.Count > 0)
        {
            _logger.LogInformation(
                "Injecting {Count} answered clarifications into LLM prompts for {FilePath}",
                merged.Count, filePath);
        }

        var endpoints = await RunPromptAsync<EndpointExtraction>(
            workspaceId, ExtractionPromptId.ExtractHttpEndpoints, filePath, trimmed,
            mergedGroundTruth, mergedClfSuffix, structuralBlock, structuralSuffix, ct);
        var publishes = await RunPromptAsync<EventPublisherExtraction>(
            workspaceId, ExtractionPromptId.ExtractEventPublishers, filePath, trimmed,
            mergedGroundTruth, mergedClfSuffix, structuralBlock, structuralSuffix, ct);
        var consumes = await RunPromptAsync<EventConsumerExtraction>(
            workspaceId, ExtractionPromptId.ExtractEventConsumers, filePath, trimmed,
            mergedGroundTruth, mergedClfSuffix, structuralBlock, structuralSuffix, ct);
        var storage = await RunPromptAsync<StorageOwnershipExtraction>(
            workspaceId, ExtractionPromptId.ExtractStorageOwnership, filePath, trimmed,
            mergedGroundTruth, mergedClfSuffix, structuralBlock, structuralSuffix, ct);
        var conventions = await RunPromptAsync<ConventionExtraction>(
            workspaceId, ExtractionPromptId.InferConventions, filePath, trimmed,
            mergedGroundTruth, mergedClfSuffix, structuralBlock, structuralSuffix, ct);
        var contracts = await RunPromptAsync<IntegrationContractsExtraction>(
            workspaceId, ExtractionPromptId.ExtractIntegrationContracts, filePath, trimmed,
            mergedGroundTruth, mergedClfSuffix, structuralBlock, structuralSuffix, ct);

        var aggregate = new FileExtractionRecord(
            service, endpoints, publishes, consumes, storage, conventions, contracts);

        await _repository.UpsertAsync(workspaceId, repoId, filePath, contentHash, aggregate, ct);

        // Sprint 3 wiring: project the aggregated extraction into the AGE graph.
        // We swallow failures here — the cache + file_extractions row already
        // persisted successfully, and the graph layer is a derived view that
        // can be rebuilt from JSONB rows on the next scan.
        try
        {
            await WriteGraphProjectionAsync(workspaceId, repoId, filePath, contentHash, aggregate, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Graph projection failed workspace={WorkspaceId} repo={RepoId} file={FilePath}; continuing.",
                workspaceId, repoId, filePath);
        }

        _logger.LogInformation(
            "LLM extraction complete workspace={WorkspaceId} repo={RepoId} file={FilePath}",
            workspaceId, repoId, filePath);
    }

    /// <summary>
    /// Translates the per-file <see cref="FileExtractionRecord"/> into a set of
    /// vertex + edge upserts against the AGE graph. All writes execute inside a
    /// single transaction so a mid-projection failure leaves the graph in its
    /// pre-extraction state for this file.
    /// </summary>
    /// <remarks>
    /// Node identity uses a deterministic Guid scheme keyed by
    /// MD5(workspaceId + label + canonical-key) so that re-running extraction
    /// on the same logical entity targets the same vertex. This is an MVP
    /// shortcut; see TODO below.
    /// </remarks>
    private async Task WriteGraphProjectionAsync(
        Guid workspaceId,
        Guid repoId,
        string filePath,
        string contentHash,
        FileExtractionRecord aggregate,
        CancellationToken ct)
    {
        // last_extraction_id discriminates "freshly upserted" vs "stale" nodes
        // for the same file_path. RemoveOrphansForFileAsync (used by DiffScanJob
        // after a file changes) deletes any node whose discriminator no longer
        // matches the current run.
        // TODO: Replace deterministic-Guid scheme with proper graph identity
        // lookup post-MVP — at that point last_extraction_id stops being a
        // file:hash pair and becomes a per-extraction-run id.
        var extractionId = DeterministicGuid($"{filePath}:{contentHash}");

        await _graphWriter.ExecuteInTransactionAsync(async session =>
        {
            Guid? serviceId = null;

            // ---- Service ----
            if (aggregate.Service?.IsPartOfService == true
                && !string.IsNullOrWhiteSpace(aggregate.Service.ServiceName))
            {
                serviceId = DeterministicGuid($"Service|{workspaceId}|{aggregate.Service.ServiceName}");
                await session.UpsertNodeAsync(new GraphNodeSpec(
                    workspaceId,
                    "Service",
                    serviceId.Value,
                    new Dictionary<string, object?>
                    {
                        ["name"] = aggregate.Service.ServiceName,
                        ["purpose"] = aggregate.Service.ServicePurpose,
                        ["repo_id"] = repoId.ToString(),
                        ["root_path"] = aggregate.Service.RootPath,
                        ["tech_stack"] = aggregate.Service.TechStack,
                        ["workspace_id"] = workspaceId.ToString(),
                        ["last_extraction_id"] = extractionId.ToString(),
                        ["file_path"] = filePath,
                    },
                    extractionId), ct);
            }

            // ---- Endpoints (EXPOSES) ----
            if (aggregate.Endpoints?.Endpoints is { Count: > 0 } endpoints)
            {
                foreach (var ep in endpoints)
                {
                    if (string.IsNullOrWhiteSpace(ep.Method) || string.IsNullOrWhiteSpace(ep.Path))
                        continue;

                    var endpointId = DeterministicGuid($"Endpoint|{workspaceId}|{ep.Method}|{ep.Path}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Endpoint",
                        endpointId,
                        new Dictionary<string, object?>
                        {
                            ["method"] = ep.Method,
                            ["path"] = ep.Path,
                            ["handler_file"] = filePath,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "EXPOSES", serviceId.Value, endpointId), ct);
                    }
                }
            }

            // ---- Events published (PUBLISHES) ----
            if (aggregate.EventsPublished?.Publishes is { Count: > 0 } published)
            {
                foreach (var evt in published)
                {
                    if (string.IsNullOrWhiteSpace(evt.Name)) continue;
                    var eventId = DeterministicGuid($"Event|{workspaceId}|{evt.Name}|{evt.Version}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Event",
                        eventId,
                        new Dictionary<string, object?>
                        {
                            ["name"] = evt.Name,
                            ["version"] = evt.Version,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "PUBLISHES", serviceId.Value, eventId), ct);
                    }
                }
            }

            // ---- Events consumed (CONSUMES) ----
            if (aggregate.EventsConsumed?.Consumes is { Count: > 0 } consumed)
            {
                foreach (var evt in consumed)
                {
                    if (string.IsNullOrWhiteSpace(evt.Name)) continue;
                    var eventId = DeterministicGuid($"Event|{workspaceId}|{evt.Name}|{evt.Version}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Event",
                        eventId,
                        new Dictionary<string, object?>
                        {
                            ["name"] = evt.Name,
                            ["version"] = evt.Version,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "CONSUMES", serviceId.Value, eventId), ct);
                    }
                }
            }

            // ---- Storage (OWNS / READS) ----
            if (aggregate.Storage?.Storages is { Count: > 0 } storages)
            {
                foreach (var storage in storages)
                {
                    if (string.IsNullOrWhiteSpace(storage.Name)) continue;
                    var storageId = DeterministicGuid($"Storage|{workspaceId}|{storage.Name}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Storage",
                        storageId,
                        new Dictionary<string, object?>
                        {
                            ["name"] = storage.Name,
                            ["type"] = storage.Type,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        var edgeLabel = string.Equals(storage.Access, "owns", StringComparison.OrdinalIgnoreCase)
                            ? "WRITES_TO"
                            : "READS_FROM";
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, edgeLabel, serviceId.Value, storageId), ct);
                    }
                }
            }

            // ---- Conventions (FOLLOWS) ----
            if (aggregate.Conventions?.Conventions is { Count: > 0 } conventions)
            {
                foreach (var convention in conventions)
                {
                    if (string.IsNullOrWhiteSpace(convention.Name)) continue;
                    var conventionId = DeterministicGuid(
                        $"Convention|{workspaceId}|{convention.Category}|{convention.Name}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Convention",
                        conventionId,
                        new Dictionary<string, object?>
                        {
                            ["category"] = convention.Category,
                            ["name"] = convention.Name,
                            ["description"] = convention.Description,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "FOLLOWS", serviceId.Value, conventionId), ct);
                    }
                }
            }

            // ---- Integration contracts: outbound HTTP / gRPC / messaging ----
            // Endpoint id derivation matches the inbound extractor exactly so a
            // backend exposing "POST /api/foo" and a frontend calling it land
            // on the SAME Endpoint node — CALLS edge wires cross-repo for free.
            // Same trick for Event / Queue nodes by name.
            if (aggregate.IntegrationContracts is { } contracts)
            {
                foreach (var call in contracts.HttpClientCalls ?? Array.Empty<HttpClientCall>())
                {
                    if (string.IsNullOrWhiteSpace(call.Method) || string.IsNullOrWhiteSpace(call.Path))
                        continue;
                    var method = call.Method.Trim().ToUpperInvariant();
                    var path = NormaliseHttpPath(call.Path);
                    if (path.Length == 0) continue;

                    var endpointId = DeterministicGuid($"Endpoint|{workspaceId}|{method}|{path}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Endpoint",
                        endpointId,
                        new Dictionary<string, object?>
                        {
                            ["method"] = method,
                            ["path"] = path,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "CALLS", serviceId.Value, endpointId,
                            new Dictionary<string, object?>
                            {
                                ["transport"] = "http",
                                ["base_url"] = call.BaseUrl,
                                ["evidence"] = call.Evidence,
                            }), ct);
                    }
                }

                foreach (var grpc in contracts.GrpcClientCalls ?? Array.Empty<GrpcCall>())
                {
                    if (string.IsNullOrWhiteSpace(grpc.Service) || string.IsNullOrWhiteSpace(grpc.Method))
                        continue;
                    var qualified = $"{grpc.Service}.{grpc.Method}";
                    var endpointId = DeterministicGuid($"Endpoint|{workspaceId}|grpc|{qualified}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Endpoint",
                        endpointId,
                        new Dictionary<string, object?>
                        {
                            ["method"] = "grpc",
                            ["path"] = qualified,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "CALLS", serviceId.Value, endpointId,
                            new Dictionary<string, object?>
                            {
                                ["transport"] = "grpc",
                                ["evidence"] = grpc.Evidence,
                            }), ct);
                    }
                }

                foreach (var grpc in contracts.GrpcServerImpls ?? Array.Empty<GrpcCall>())
                {
                    if (string.IsNullOrWhiteSpace(grpc.Service) || string.IsNullOrWhiteSpace(grpc.Method))
                        continue;
                    var qualified = $"{grpc.Service}.{grpc.Method}";
                    var endpointId = DeterministicGuid($"Endpoint|{workspaceId}|grpc|{qualified}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Endpoint",
                        endpointId,
                        new Dictionary<string, object?>
                        {
                            ["method"] = "grpc",
                            ["path"] = qualified,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "EXPOSES", serviceId.Value, endpointId), ct);
                    }
                }

                // SignalR hub methods (server) — EXPOSES. Endpoint id keyed on
                // signalr:HubClass.Method so a client-side invoke into the same
                // hub method collapses onto the same vertex cross-repo.
                foreach (var hub in contracts.SignalRHubMethods ?? Array.Empty<GrpcCall>())
                {
                    if (string.IsNullOrWhiteSpace(hub.Service) || string.IsNullOrWhiteSpace(hub.Method))
                        continue;
                    var qualified = $"{hub.Service}.{hub.Method}";
                    var endpointId = DeterministicGuid($"Endpoint|{workspaceId}|signalr|{qualified}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Endpoint",
                        endpointId,
                        new Dictionary<string, object?>
                        {
                            ["method"] = "signalr",
                            ["path"] = qualified,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "EXPOSES", serviceId.Value, endpointId), ct);
                    }
                }

                // SignalR client invokes — CALLS into a hub method endpoint.
                foreach (var inv in contracts.SignalRClientInvokes ?? Array.Empty<GrpcCall>())
                {
                    if (string.IsNullOrWhiteSpace(inv.Service) || string.IsNullOrWhiteSpace(inv.Method))
                        continue;
                    var qualified = $"{inv.Service}.{inv.Method}";
                    var endpointId = DeterministicGuid($"Endpoint|{workspaceId}|signalr|{qualified}");
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        "Endpoint",
                        endpointId,
                        new Dictionary<string, object?>
                        {
                            ["method"] = "signalr",
                            ["path"] = qualified,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "CALLS", serviceId.Value, endpointId,
                            new Dictionary<string, object?>
                            {
                                ["transport"] = "signalr",
                                ["evidence"] = inv.Evidence,
                            }), ct);
                    }
                }

                foreach (var ch in contracts.MessagingPublishes ?? Array.Empty<MessagingChannel>())
                {
                    var (nodeLabel, nodeId, name) = ResolveMessagingNode(workspaceId, ch);
                    if (nodeId is null) continue;
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        nodeLabel!,
                        nodeId.Value,
                        new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["kind"] = ch.Kind,
                            ["routing_key"] = ch.RoutingKey,
                            ["exchange"] = ch.Exchange,
                            ["topic"] = ch.Topic,
                            ["queue"] = ch.Queue,
                            ["message_type"] = ch.MessageType,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "PUBLISHES", serviceId.Value, nodeId.Value,
                            new Dictionary<string, object?>
                            {
                                ["transport"] = ch.Kind,
                                ["routing_key"] = ch.RoutingKey,
                            }), ct);
                    }
                }

                foreach (var ch in contracts.MessagingConsumes ?? Array.Empty<MessagingChannel>())
                {
                    var (nodeLabel, nodeId, name) = ResolveMessagingNode(workspaceId, ch);
                    if (nodeId is null) continue;
                    await session.UpsertNodeAsync(new GraphNodeSpec(
                        workspaceId,
                        nodeLabel!,
                        nodeId.Value,
                        new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["kind"] = ch.Kind,
                            ["routing_key"] = ch.RoutingKey,
                            ["exchange"] = ch.Exchange,
                            ["topic"] = ch.Topic,
                            ["queue"] = ch.Queue,
                            ["message_type"] = ch.MessageType,
                            ["workspace_id"] = workspaceId.ToString(),
                            ["last_extraction_id"] = extractionId.ToString(),
                            ["file_path"] = filePath,
                        },
                        extractionId), ct);

                    if (serviceId is not null)
                    {
                        await session.UpsertEdgeAsync(new GraphEdgeSpec(
                            workspaceId, "CONSUMES", serviceId.Value, nodeId.Value,
                            new Dictionary<string, object?>
                            {
                                ["transport"] = ch.Kind,
                                ["routing_key"] = ch.RoutingKey,
                            }), ct);
                    }
                }
            }
        }, ct);
    }

    private static string NormaliseHttpPath(string raw)
    {
        var path = raw.Trim();
        var qi = path.IndexOf('?');
        if (qi >= 0) path = path[..qi];
        var hi = path.IndexOf('#');
        if (hi >= 0) path = path[..hi];
        // strip scheme + host if present
        var schemeIdx = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var afterScheme = schemeIdx + 3;
            var slashAfterHost = path.IndexOf('/', afterScheme);
            path = slashAfterHost >= 0 ? path[slashAfterHost..] : "/";
        }
        if (!path.StartsWith('/')) path = "/" + path;
        // Collapse duplicate slashes.
        while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/");
        return path;
    }

    /// <summary>
    /// Decide which graph label + deterministic id to use for an async-messaging
    /// channel reference. Topics / queues / exchanges all collapse onto a single
    /// "name" used as the canonical wire identity so a publisher in one repo and
    /// a consumer in another land on the same node.
    /// </summary>
    private static (string? Label, Guid? Id, string Name) ResolveMessagingNode(
        Guid workspaceId, MessagingChannel ch)
    {
        var name =
            FirstNonEmpty(ch.Topic, ch.Exchange, ch.Queue, ch.MessageType, ch.RoutingKey);
        if (name is null) return (null, null, string.Empty);

        // Queue nodes for AMQP-style transports (RabbitMQ / SQS / Azure Service
        // Bus); Event nodes for topic-based / message-typed flows. Same name
        // collapses cross-repo regardless of label choice because the id
        // formula keys on (label, workspaceId, name).
        string label;
        if (!string.IsNullOrWhiteSpace(ch.Queue)
            || (ch.Kind ?? "").Contains("rabbit", StringComparison.OrdinalIgnoreCase)
            || (ch.Kind ?? "").Contains("sqs", StringComparison.OrdinalIgnoreCase))
        {
            label = "Queue";
        }
        else
        {
            label = "Event";
        }

        var id = DeterministicGuid($"{label}|{workspaceId}|{name}|");
        return (label, id, name);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    /// <summary>
    /// Build a deterministic Guid from an arbitrary string via MD5. Same string
    /// always produces the same Guid, so identical logical entities collapse
    /// onto the same vertex across re-runs.
    /// </summary>
    private static Guid DeterministicGuid(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    private async Task<T?> RunPromptAsync<T>(
        Guid workspaceId,
        ExtractionPromptId promptId,
        string filePath,
        string fileContent,
        string groundTruthBlock,
        string clarificationCacheSuffix,
        string structuralContextBlock,
        string structuralContextSuffix,
        CancellationToken ct)
        where T : class
    {
        if (!_prompts.TryGetValue(promptId, out var prompt))
        {
            _logger.LogWarning("No prompt registered for {PromptId}", promptId);
            return null;
        }

        // Cache key layers: promptId + base version + graphify structural suffix + clarification suffix.
        // promptId must be in the key because all 7 prompts share the same file content,
        // same model, and (today) the same version string — without it they all collide on
        // one cache row and later prompts get a deserialized version of the first prompt's
        // result (silently null fields). Each suffix is "" when absent.
        var effectivePromptVersion = $"{promptId}|{prompt.Version}";
        if (!string.IsNullOrEmpty(structuralContextSuffix))
            effectivePromptVersion += structuralContextSuffix;
        if (!string.IsNullOrEmpty(clarificationCacheSuffix))
            effectivePromptVersion += clarificationCacheSuffix;

        var cacheKey = _cache.ComputeKey(fileContent, effectivePromptVersion, ModelId);

        var cached = await _cache.GetAsync<T>(cacheKey, ct);
        if (cached is not null)
        {
            return cached;
        }

        var userPrompt = prompt.UserPromptTemplate
            .Replace("{file_path}", filePath, StringComparison.Ordinal)
            .Replace("{file_content}", fileContent, StringComparison.Ordinal);

        // Graphify structural context first — gives the LLM the AST-level picture
        // (classes, functions, call edges) before any higher-level instructions.
        if (!string.IsNullOrEmpty(structuralContextBlock))
        {
            userPrompt = userPrompt + "\n\n" + structuralContextBlock;
        }

        // BE-040: append ground-truth section so the LLM uses human-resolved
        // answers as authoritative. Skipped when there are no clarifications
        // so the on-the-wire prompt is byte-identical to the pre-BE-040 form.
        if (!string.IsNullOrEmpty(groundTruthBlock))
        {
            userPrompt = userPrompt + "\n\n" + groundTruthBlock;
        }

        try
        {
            var llmResult = await _router.RouteStructuredAsync<T>(
                LlmTaskType.Extraction,
                prompt.SystemPrompt,
                userPrompt,
                prompt.ToolName,
                prompt.ToolDescription,
                prompt.OutputJsonSchema,
                ct);

            if (llmResult.Output is not null)
            {
                await _cache.SetAsync(
                    cacheKey,
                    workspaceId,
                    ModelId,
                    effectivePromptVersion,
                    llmResult.Output,
                    ct);
            }

            return llmResult.Output;
        }
        catch (Exception ex)
        {
            // Per-prompt failure does not blow up the whole job — we still want
            // the partial aggregate persisted. Hangfire retry handles transient
            // failures at the job level.
            _logger.LogWarning(
                ex,
                "Prompt {PromptId} failed for {FilePath}; recording null partial result.",
                promptId,
                filePath);
            return null;
        }
    }

    /// <summary>
    /// BE-040: format answered clarifications as a Markdown "Known Ground Truth"
    /// section and compute a stable cache-key suffix so the same clarification
    /// set always reuses the same cache entry. When no clarifications apply,
    /// both outputs are empty strings — the caller MUST treat empty as
    /// "do nothing", preserving the pre-BE-040 cache key + prompt body.
    /// </summary>
    private static (string GroundTruthBlock, string CacheSuffix) BuildGroundTruth(
        IReadOnlyList<AnsweredClarification> clarifications)
    {
        if (clarifications is null || clarifications.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var sb = new StringBuilder();
        sb.Append("## Known Ground Truth (from prior human clarifications)\n");
        foreach (var c in clarifications)
        {
            sb.Append("- Q: ").Append(c.Question).Append('\n');
            sb.Append("  A: ").Append(c.Answer).Append('\n');
        }

        var block = sb.ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(block));
        // 12 hex chars = 48 bits — plenty to avoid collisions across the
        // O(few) distinct clarification sets seen per file.
        var suffix = "+clf:" + Convert.ToHexString(hash).ToLowerInvariant()[..12];
        return (block, suffix);
    }

    /// <summary>
    /// Formats Graphify AST nodes and edges as a Markdown structural context
    /// block appended to LLM prompts. The cache suffix changes when the set of
    /// structural nodes changes (e.g. after a graphify re-run), ensuring cached
    /// LLM results are invalidated when the structural picture shifts.
    /// Returns empty strings when context is absent so callers produce the same
    /// prompt and cache key as before graphify integration.
    /// </summary>
    private static (string Block, string CacheSuffix) BuildStructuralContext(GraphifyFileContext context)
    {
        if (context.IsEmpty)
            return (string.Empty, string.Empty);

        var sb = new StringBuilder();
        sb.Append("## Structural Context (from AST analysis)\n");
        sb.Append("The following entities and relationships were extracted from this file's AST ");
        sb.Append("by a static analysis pass. Use them to inform your extraction — do not invent ");
        sb.Append("entities that contradict this structural evidence.\n\n");

        sb.Append("Entities:\n");
        foreach (var node in context.Nodes)
        {
            var displayName = node.Name ?? node.Id;
            sb.Append("- [").Append(node.Type).Append("] ").Append(displayName).Append('\n');
        }

        if (context.OutboundEdges.Count > 0)
        {
            sb.Append("\nRelationships (source → relation → target):\n");
            // Cap at 40 edges to avoid runaway token growth on dense call graphs.
            foreach (var edge in context.OutboundEdges.Take(40))
            {
                sb.Append("- ").Append(edge.Source)
                  .Append(" --[").Append(edge.Type).Append("]--> ")
                  .Append(edge.Target).Append('\n');
            }
        }

        var block = sb.ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(block));
        var suffix = "+gfy:" + Convert.ToHexString(hash).ToLowerInvariant()[..12];
        return (block, suffix);
    }

}
