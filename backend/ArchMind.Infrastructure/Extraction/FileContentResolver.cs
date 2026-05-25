using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Cloning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Extraction;

/// <summary>
/// Filesystem-backed <see cref="IFileContentResolver"/>. Resolves
/// <c>{WorkingDirRoot}/{workspaceId}/repos/{repoId}/{relativePath}</c> and reads
/// the file as UTF-8 text. Skips files larger than 1 MiB to keep extraction
/// bounded in the MVP.
/// </summary>
public sealed class FileContentResolver : IFileContentResolver
{
    /// <summary>Maximum file size (bytes) the resolver will read. Larger files return empty.</summary>
    public const long MaxFileSizeBytes = 1024L * 1024L; // 1 MiB

    private readonly CloningOptions _options;
    private readonly ILogger<FileContentResolver> _logger;

    public FileContentResolver(IOptions<CloningOptions> options, ILogger<FileContentResolver> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> ReadAsync(
        Guid workspaceId,
        Guid repoId,
        string relativePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path must be provided.", nameof(relativePath));
        }

        // Defensive: reject path-traversal attempts so a malicious filePath cannot
        // escape the workspace/repo working tree.
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new ArgumentException("Relative path may not contain '..' segments.", nameof(relativePath));
        }

        var repoRoot = Path.Combine(
            _options.WorkingDirRoot,
            workspaceId.ToString(),
            "repos",
            repoId.ToString());

        var absolute = Path.GetFullPath(Path.Combine(repoRoot, normalized));

        // Belt-and-braces: ensure the resolved path is still inside repoRoot.
        var normalizedRepoRoot = Path.GetFullPath(repoRoot);
        if (!absolute.StartsWith(normalizedRepoRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !absolute.Equals(normalizedRepoRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("Relative path resolved outside of the repo working directory.", nameof(relativePath));
        }

        if (!File.Exists(absolute))
        {
            throw new FileNotFoundException($"File not found: {relativePath}", absolute);
        }

        var info = new FileInfo(absolute);
        if (info.Length > MaxFileSizeBytes)
        {
            _logger.LogWarning(
                "Skipping LLM extraction for oversized file {FilePath} ({Bytes} bytes > {MaxBytes} bytes).",
                relativePath,
                info.Length,
                MaxFileSizeBytes);
            return string.Empty;
        }

        return await File.ReadAllTextAsync(absolute, System.Text.Encoding.UTF8, ct);
    }
}
