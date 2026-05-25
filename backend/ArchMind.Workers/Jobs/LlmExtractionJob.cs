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
    private readonly IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> _prompts;
    private readonly ILogger<LlmExtractionJob> _logger;

    public LlmExtractionJob(
        ILlmRouter router,
        ILlmExtractionCacheService cache,
        IFileContentResolver files,
        IFileExtractionRepository repository,
        IGraphWriter graphWriter,
        IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> prompts,
        ILogger<LlmExtractionJob> logger)
    {
        _router = router;
        _cache = cache;
        _files = files;
        _repository = repository;
        _graphWriter = graphWriter;
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

        var service = await RunPromptAsync<ServiceExtraction>(
            workspaceId, ExtractionPromptId.IdentifyService, filePath, trimmed, ct);
        var endpoints = await RunPromptAsync<EndpointExtraction>(
            workspaceId, ExtractionPromptId.ExtractHttpEndpoints, filePath, trimmed, ct);
        var publishes = await RunPromptAsync<EventPublisherExtraction>(
            workspaceId, ExtractionPromptId.ExtractEventPublishers, filePath, trimmed, ct);
        var consumes = await RunPromptAsync<EventConsumerExtraction>(
            workspaceId, ExtractionPromptId.ExtractEventConsumers, filePath, trimmed, ct);
        var storage = await RunPromptAsync<StorageOwnershipExtraction>(
            workspaceId, ExtractionPromptId.ExtractStorageOwnership, filePath, trimmed, ct);
        var conventions = await RunPromptAsync<ConventionExtraction>(
            workspaceId, ExtractionPromptId.InferConventions, filePath, trimmed, ct);

        var aggregate = new FileExtractionRecord(
            service, endpoints, publishes, consumes, storage, conventions);

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
                            ? "OWNS"
                            : "READS";
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
        }, ct);
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
        CancellationToken ct)
        where T : class
    {
        if (!_prompts.TryGetValue(promptId, out var prompt))
        {
            _logger.LogWarning("No prompt registered for {PromptId}", promptId);
            return null;
        }

        var cacheKey = _cache.ComputeKey(fileContent, prompt.Version, ModelId);

        var cached = await _cache.GetAsync<T>(cacheKey, ct);
        if (cached is not null)
        {
            return cached;
        }

        var userPrompt = prompt.UserPromptTemplate
            .Replace("{file_path}", filePath, StringComparison.Ordinal)
            .Replace("{file_content}", fileContent, StringComparison.Ordinal);

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
                    prompt.Version,
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

}
