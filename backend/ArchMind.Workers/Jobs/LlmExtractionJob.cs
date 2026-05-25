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
    private readonly IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> _prompts;
    private readonly ILogger<LlmExtractionJob> _logger;

    public LlmExtractionJob(
        ILlmRouter router,
        ILlmExtractionCacheService cache,
        IFileContentResolver files,
        IFileExtractionRepository repository,
        IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> prompts,
        ILogger<LlmExtractionJob> logger)
    {
        _router = router;
        _cache = cache;
        _files = files;
        _repository = repository;
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

        _logger.LogInformation(
            "LLM extraction complete workspace={WorkspaceId} repo={RepoId} file={FilePath}",
            workspaceId, repoId, filePath);
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
