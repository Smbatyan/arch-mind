namespace ArchMind.Infrastructure.Graphify;

/// <summary>
/// Configuration for <see cref="WorkspaceGraphService"/>. Bound to the
/// <c>WorkspaceGraph</c> configuration section.
/// </summary>
public sealed class WorkspaceGraphOptions
{
    /// <summary>
    /// Python interpreter used to run the combine script.
    /// Defaults to "python3" (assumed on PATH).
    /// </summary>
    public string PythonExecutable { get; set; } = "python3";

    /// <summary>
    /// Absolute or relative path to <c>combine_workspace_graph.py</c>.
    /// Relative paths are resolved from the process working directory.
    /// </summary>
    public string CombineScriptPath { get; set; } = "scripts/combine_workspace_graph.py";

    /// <summary>
    /// Hard timeout for the Python subprocess. Defaults to 5 minutes —
    /// merging large graphs is CPU-bound but never requires network/LLM calls.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}
