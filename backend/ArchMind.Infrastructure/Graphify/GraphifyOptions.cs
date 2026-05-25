namespace ArchMind.Infrastructure.Graphify;

/// <summary>
/// Configuration for <see cref="GraphifyRunner"/>. Bound to the <c>Graphify</c>
/// configuration section.
/// </summary>
public sealed class GraphifyOptions
{
    /// <summary>
    /// Path to the Graphify executable. Defaults to "graphify" (assumed on PATH).
    /// In the production container this is <c>/usr/local/bin/graphify</c>.
    /// </summary>
    public string Executable { get; set; } = "graphify";

    /// <summary>
    /// Hard timeout for the Graphify subprocess. Defaults to 30 minutes.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Subdirectory beneath the repo path where Graphify writes its output.
    /// </summary>
    public string OutputSubdirectory { get; set; } = "graphify-out";

    /// <summary>
    /// File name of the structural graph JSON Graphify produces.
    /// </summary>
    public string OutputFileName { get; set; } = "graph.json";
}
