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
}
