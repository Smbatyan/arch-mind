using System.Text.Json;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Extraction;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Infrastructure.Extraction;

/// <summary>
/// EF Core / Postgres-backed <see cref="IFileExtractionRepository"/>. Upserts via
/// raw SQL on the natural key (workspace_id, repo_id, file_path).
/// </summary>
public sealed class FileExtractionRepository : IFileExtractionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ArchMindDbContext _db;

    public FileExtractionRepository(ArchMindDbContext db)
    {
        _db = db;
    }

    public async Task UpsertAsync(
        Guid workspaceId,
        Guid repoId,
        string filePath,
        string contentHash,
        FileExtractionRecord record,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(record, JsonOptions);

        const string sql = """
            INSERT INTO file_extractions
                (id, workspace_id, repo_id, file_path, content_hash, extraction_payload, created_at)
            VALUES
                (gen_random_uuid(), {0}, {1}, {2}, {3}, {4}::jsonb, now())
            ON CONFLICT (workspace_id, repo_id, file_path) DO UPDATE
            SET content_hash = EXCLUDED.content_hash,
                extraction_payload = EXCLUDED.extraction_payload,
                created_at = now();
        """;

        await _db.Database.ExecuteSqlRawAsync(
            sql,
            new object[] { workspaceId, repoId, filePath, contentHash, payload },
            ct);
    }

    public async Task<IReadOnlyList<FileExtractionRow>> GetLatestForFilesAsync(
        Guid workspaceId,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            return Array.Empty<FileExtractionRow>();
        }

        // EF translates Contains() to "= ANY(@p)" against the parameter array
        // on Npgsql, which keeps the query single-statement and indexable on
        // (workspace_id, repo_id, file_path).
        var rows = await _db.FileExtractions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && filePaths.Contains(x.FilePath))
            .Select(x => new { x.FilePath, x.ContentHash, x.ExtractionPayload })
            .ToListAsync(ct);

        var result = new List<FileExtractionRow>(rows.Count);
        foreach (var r in rows)
        {
            FileExtractionRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<FileExtractionRecord>(r.ExtractionPayload, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip rows we can't deserialize rather than blowing up the whole
                // get_relevant_context call.
                continue;
            }
            if (record is null) continue;
            result.Add(new FileExtractionRow(r.FilePath, r.ContentHash, record));
        }
        return result;
    }

    public async Task<IReadOnlyList<FileExtractionRow>> SearchByTokensAsync(
        Guid workspaceId,
        IReadOnlyList<string> tokens,
        int limit,
        CancellationToken ct = default)
    {
        if (tokens is null || tokens.Count == 0)
            return Array.Empty<FileExtractionRow>();

        // Build ILIKE patterns: "%token%" for each token.
        // Escape any literal % or _ in the token to avoid false wildcard matches.
        var patterns = tokens
            .Select(t => "%" + t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%")
            .ToArray();

        // Query rows that:
        //   1. Belong to the workspace.
        //   2. Have at least one of the relevant extraction sections present.
        //   3. Match any of the search patterns (case-insensitive text search on the full payload).
        // Restricting to rows that have Endpoints/EventsPublished/EventsConsumed keeps the
        // result set focused — we don't want pure-convention or pure-service-identity rows.
        var rows = await _db.FileExtractions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .Where(x =>
                x.ExtractionPayload.Contains("\"Endpoints\"") ||
                x.ExtractionPayload.Contains("\"EventsPublished\"") ||
                x.ExtractionPayload.Contains("\"EventsConsumed\""))
            .Where(x => patterns.Any(p => EF.Functions.ILike(x.ExtractionPayload, p)))
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x => new { x.FilePath, x.ContentHash, x.ExtractionPayload })
            .ToListAsync(ct);

        var result = new List<FileExtractionRow>(rows.Count);
        foreach (var r in rows)
        {
            FileExtractionRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<FileExtractionRecord>(r.ExtractionPayload, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (record is null) continue;
            result.Add(new FileExtractionRow(r.FilePath, r.ContentHash, record));
        }
        return result;
    }
}
