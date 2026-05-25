using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Services;

/// <summary>
/// EF Core / Postgres-backed implementation of the LLM extraction cache.
/// Stores results as jsonb and dedupes by SHA-256(fileContent|promptVersion|modelId).
/// </summary>
public sealed class LlmExtractionCacheService : ILlmExtractionCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ArchMindDbContext _db;
    private readonly ILogger<LlmExtractionCacheService> _logger;

    public LlmExtractionCacheService(ArchMindDbContext db, ILogger<LlmExtractionCacheService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string ComputeKey(string fileContent, string promptVersion, string modelId)
    {
        var payload = $"{fileContent}|{promptVersion}|{modelId}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<T?> GetAsync<T>(string contentHash, CancellationToken ct = default) where T : class
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return null;
        }

        var resultJson = await _db.LlmExtractionCache
            .AsNoTracking()
            .Where(x => x.ContentHash == contentHash)
            .Select(x => x.Result)
            .FirstOrDefaultAsync(ct);

        if (resultJson is null)
        {
            return null;
        }

        // Best-effort hit count increment; failure to increment must not break callers.
        try
        {
            await _db.LlmExtractionCache
                .Where(x => x.ContentHash == contentHash)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.HitCount, x => x.HitCount + 1), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to increment hit count for cache entry {HashPrefix}", HashPrefix(contentHash));
        }

        _logger.LogDebug("LLM cache hit {HashPrefix}", HashPrefix(contentHash));

        try
        {
            return JsonSerializer.Deserialize<T>(resultJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize cached LLM result for {HashPrefix}", HashPrefix(contentHash));
            return null;
        }
    }

    public async Task SetAsync<T>(
        string contentHash,
        Guid workspaceId,
        string model,
        string promptVersion,
        T result,
        CancellationToken ct = default) where T : class
    {
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);

        // Upsert via raw SQL — simpler than juggling EF tracking for this PK conflict.
        const string sql = """
            INSERT INTO llm_extraction_cache (content_hash, workspace_id, model, prompt_version, result, hit_count, created_at)
            VALUES ({0}, {1}, {2}, {3}, {4}::jsonb, 0, now())
            ON CONFLICT (content_hash) DO UPDATE
            SET model = EXCLUDED.model,
                prompt_version = EXCLUDED.prompt_version,
                result = EXCLUDED.result;
        """;

        await _db.Database.ExecuteSqlRawAsync(
            sql,
            new object[] { contentHash, workspaceId, model, promptVersion, resultJson },
            ct);
    }

    private static string HashPrefix(string contentHash) =>
        contentHash.Length <= 8 ? contentHash : contentHash[..8];
}
