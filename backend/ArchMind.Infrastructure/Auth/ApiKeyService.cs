using System.Security.Cryptography;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Infrastructure.Auth;

/// <summary>
/// EF Core-backed <see cref="IApiKeyService"/>. Tokens have the shape
/// <c>am_&lt;43 base64url chars&gt;</c> (256 bits of entropy). Only the
/// SHA-256 hex digest is stored.
/// </summary>
public sealed class ApiKeyService : IApiKeyService
{
    private const string Prefix = "am_";
    private const int RawKeyBytes = 32;
    private const int DisplayPrefixLength = 8;

    private readonly ArchMindDbContext _db;

    public ApiKeyService(ArchMindDbContext db)
    {
        _db = db;
    }

    public async Task<(WorkspaceApiKey Entity, string Plaintext)> CreateAsync(
        Guid workspaceId,
        string name,
        CancellationToken ct)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("workspaceId must be set.", nameof(workspaceId));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name must not be empty.", nameof(name));
        }

        var randomBytes = RandomNumberGenerator.GetBytes(RawKeyBytes);
        var plaintext = Prefix + Base64UrlEncode(randomBytes);
        var keyHash = ComputeSha256Hex(plaintext);
        var keyPrefix = plaintext[..DisplayPrefixLength];

        var entity = new WorkspaceApiKey
        {
            WorkspaceId = workspaceId,
            Name = name.Trim(),
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.WorkspaceApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);

        return (entity, plaintext);
    }

    public async Task<WorkspaceApiKey?> ValidateAsync(string plaintext, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(plaintext) || !plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var keyHash = ComputeSha256Hex(plaintext);

        var entity = await _db.WorkspaceApiKeys
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.RevokedAt == null, ct);

        if (entity is null)
        {
            return null;
        }

        // Update LastUsedAt synchronously. Errors here would surface to the
        // caller; if that becomes a hotspot, swap for a fire-and-forget queue.
        entity.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return entity;
    }

    public async Task RevokeAsync(Guid keyId, CancellationToken ct)
    {
        var entity = await _db.WorkspaceApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (entity is null || entity.RevokedAt is not null)
        {
            return;
        }

        entity.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkspaceApiKey>> ListAsync(Guid workspaceId, CancellationToken ct)
    {
        return await _db.WorkspaceApiKeys
            .AsNoTracking()
            .Where(k => k.WorkspaceId == workspaceId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
