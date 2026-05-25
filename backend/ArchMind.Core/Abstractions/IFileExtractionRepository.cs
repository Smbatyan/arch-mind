using ArchMind.Core.Extraction;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// Persists the aggregated <see cref="FileExtractionRecord"/> produced by the
/// LLM extraction job. Idempotent on (workspaceId, repoId, filePath): a second
/// call with the same key updates the existing row.
/// </summary>
public interface IFileExtractionRepository
{
    Task UpsertAsync(
        Guid workspaceId,
        Guid repoId,
        string filePath,
        string contentHash,
        FileExtractionRecord record,
        CancellationToken ct = default);
}
