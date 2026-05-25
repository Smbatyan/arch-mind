namespace ArchMind.Core.Abstractions;

/// <summary>
/// Resolves the textual content of a file inside a cloned repo working directory.
/// Abstracted so the extraction job is testable without touching the filesystem.
/// </summary>
public interface IFileContentResolver
{
    /// <summary>
    /// Read the file at <paramref name="relativePath"/> (relative to the repo root)
    /// as UTF-8 text. Implementations should return an empty string and log a
    /// warning for files exceeding an internal size cap.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist on disk.</exception>
    Task<string> ReadAsync(Guid workspaceId, Guid repoId, string relativePath, CancellationToken ct = default);
}
