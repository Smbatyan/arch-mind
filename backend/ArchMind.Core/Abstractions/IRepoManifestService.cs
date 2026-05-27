namespace ArchMind.Core.Abstractions;

/// <summary>
/// Reads dependency manifests (csproj / package.json / Directory.Packages.props /
/// pyproject.toml) from a cloned repo working tree and produces a deterministic
/// summary used to: synthesise a Service node when LLM extraction failed to
/// identify one, and emit SHARES_PACKAGE_WITH cross-repo edges. No LLM calls;
/// pure file parsing. Never throws — missing or malformed manifests return
/// <see cref="RepoManifest.Empty"/>.
/// </summary>
public interface IRepoManifestService
{
    Task<RepoManifest> ReadAsync(
        Guid workspaceId, Guid repoId, CancellationToken ct = default);
}

public sealed record RepoManifest(
    string Name,
    string Kind,
    string Language,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> InternalPackages)
{
    public static readonly RepoManifest Empty =
        new("unknown", "unknown", "unknown",
            Array.Empty<string>(), Array.Empty<string>());

    public bool IsEmpty =>
        Packages.Count == 0 && InternalPackages.Count == 0
        && string.Equals(Name, "unknown", StringComparison.Ordinal);
}
