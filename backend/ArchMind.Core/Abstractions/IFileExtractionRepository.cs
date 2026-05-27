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

    /// <summary>
    /// BE-031: fetch the latest extraction payload for each of the given paths.
    /// Path matching is exact; missing paths are simply omitted from the result.
    /// </summary>
    Task<IReadOnlyList<FileExtractionRow>> GetLatestForFilesAsync(
        Guid workspaceId,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default);

    /// <summary>
    /// Full-text search over extraction payloads for files that contain endpoints,
    /// events, or storage declarations matching any of the given tokens (case-insensitive).
    /// Used by <c>get_relevant_context</c> to surface endpoint/event hits from the
    /// incremental extraction layer — always fresher than the graphify structural
    /// graph (updated every 30 min via DiffScan vs. full-rescan only).
    /// </summary>
    Task<IReadOnlyList<FileExtractionRow>> SearchByTokensAsync(
        Guid workspaceId,
        IReadOnlyList<string> tokens,
        int limit,
        CancellationToken ct = default);
}

/// <summary>BE-031: read DTO returned by <see cref="IFileExtractionRepository.GetLatestForFilesAsync"/>.</summary>
public sealed record FileExtractionRow(
    string FilePath,
    string ContentHash,
    FileExtractionRecord Record);
