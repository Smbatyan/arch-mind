using ArchMind.Core.Entities;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// Issues, validates, lists, and revokes <see cref="WorkspaceApiKey"/> bearer
/// tokens used by external MCP clients to authenticate against the workspace.
/// </summary>
public interface IApiKeyService
{
    /// <summary>
    /// Generates a new API key. The plaintext is returned exactly once; callers
    /// MUST surface it to the user immediately and discard it after the
    /// response is written. The database only ever stores the SHA-256 hash.
    /// </summary>
    Task<(WorkspaceApiKey Entity, string Plaintext)> CreateAsync(
        Guid workspaceId,
        string name,
        CancellationToken ct);

    /// <summary>
    /// Hashes the incoming plaintext and returns the matching active (non-revoked)
    /// key, or null. Updates LastUsedAt as a side effect on success.
    /// </summary>
    Task<WorkspaceApiKey?> ValidateAsync(string plaintext, CancellationToken ct);

    /// <summary>
    /// Marks the key with the given id as revoked (sets RevokedAt to now).
    /// No-op if the key does not exist or is already revoked.
    /// </summary>
    Task RevokeAsync(Guid keyId, CancellationToken ct);

    /// <summary>
    /// Returns the metadata for every key belonging to the workspace, including
    /// revoked ones. Does NOT include the plaintext token.
    /// </summary>
    Task<IReadOnlyList<WorkspaceApiKey>> ListAsync(Guid workspaceId, CancellationToken ct);
}
