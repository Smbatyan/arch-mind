namespace ArchMind.Core.Abstractions;

/// <summary>
/// Cache for deterministic LLM extraction results, keyed by a SHA-256 hash of
/// (file content + prompt version + model id). Cache hits avoid redundant LLM calls.
/// </summary>
public interface ILlmExtractionCacheService
{
    /// <summary>
    /// Look up a cached extraction result by content hash. Increments hit count
    /// when a row is found. Returns null on miss.
    /// </summary>
    Task<T?> GetAsync<T>(string contentHash, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Upsert a cached extraction result. Overwrites Result/Model/PromptVersion
    /// on existing rows (PK conflict on contentHash).
    /// </summary>
    Task SetAsync<T>(string contentHash, Guid workspaceId, string model, string promptVersion, T result, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Compute the cache key (lowercase hex SHA-256) for the given inputs.
    /// </summary>
    string ComputeKey(string fileContent, string promptVersion, string modelId);
}
