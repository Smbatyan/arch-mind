using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Graphify;

/// <summary>
/// Spawns the Graphify CLI (https://github.com/safishamsi/graphify) as a subprocess
/// to extract a structural AST graph from a cloned repository. Uses graphify's
/// LLM-free <c>update</c> subcommand — ArchMind's own <c>LlmExtractionJob</c>
/// layers semantic enrichment on top, so graphify never needs an Anthropic key.
/// </summary>
public sealed class GraphifyRunner : IGraphifyRunner
{
    private readonly GraphifyOptions _options;
    private readonly ILogger<GraphifyRunner> _logger;

    public GraphifyRunner(
        IOptions<GraphifyOptions> options,
        ILogger<GraphifyRunner> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GraphifyOutput> RunAsync(string repoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("repoPath must be a non-empty absolute path", nameof(repoPath));
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Graphify repoPath does not exist: {repoPath}");

        _logger.LogInformation("Graphify: starting extraction for repo {RepoPath}", repoPath);
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = _options.Executable,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // graphify update <repoPath> --no-cluster
        // `update` re-extracts code files via tree-sitter only (no LLM) — per
        // `graphify --help`: "re-extract code files and update the graph (no LLM
        // needed)". --no-cluster skips the post-extraction clustering pass since
        // ArchMind layers its own LlmExtractionJob on top for semantic enrichment
        // and doesn't consume graphify clusters.
        psi.ArgumentList.Add("update");
        psi.ArgumentList.Add(repoPath);
        psi.ArgumentList.Add("--no-cluster");

        // Intentionally NOT passing ANTHROPIC_API_KEY. `update` is structural-only;
        // semantic extraction is handled separately by ArchMind.Workers.Jobs.LlmExtractionJob.

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graphify: failed to start process {Executable}", _options.Executable);
            throw new GraphifyExecutionException(
                $"Failed to start Graphify executable '{_options.Executable}': {ex.Message}",
                stderr: string.Empty,
                inner: ex);
        }

        if (process is null)
        {
            throw new GraphifyExecutionException(
                $"Process.Start returned null for '{_options.Executable}'",
                stderr: string.Empty);
        }

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKillTree(process);
            sw.Stop();
            var stderrSoFar = stderrBuf.ToString();
            _logger.LogError(
                "Graphify: timed out after {TimeoutSeconds}s for {RepoPath}. Stderr: {Stderr}",
                _options.TimeoutSeconds, repoPath, stderrSoFar);
            throw new GraphifyTimeoutException(
                $"Graphify exceeded timeout of {_options.TimeoutSeconds}s on '{repoPath}'",
                _options.TimeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            throw;
        }

        // Flush async readers so we capture trailing output.
        process.WaitForExit();
        sw.Stop();

        var stderr = stderrBuf.ToString();
        var exitCode = process.ExitCode;

        if (exitCode != 0)
        {
            _logger.LogError(
                "Graphify: exited with code {ExitCode} for {RepoPath} after {ElapsedMs}ms. Stderr: {Stderr}",
                exitCode, repoPath, sw.ElapsedMilliseconds, stderr);
            throw new GraphifyExecutionException(
                $"Graphify exited with code {exitCode} for '{repoPath}'",
                stderr);
        }

        _logger.LogInformation(
            "Graphify: completed extraction for {RepoPath} in {ElapsedMs}ms",
            repoPath, sw.ElapsedMilliseconds);

        var outputPath = Path.Combine(repoPath, _options.OutputSubdirectory, _options.OutputFileName);
        if (!File.Exists(outputPath))
        {
            throw new GraphifyOutputMalformedException(
                $"Graphify completed with exit 0 but output file is missing: {outputPath}");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new GraphifyOutputMalformedException(
                $"Failed to read Graphify output file {outputPath}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new GraphifyOutputMalformedException(
                $"Graphify output file is empty: {outputPath}");
        }

        return ParseGraphifyOutput(json, outputPath);
    }

    // =========================================================================
    // !!! TODO(graphify-schema) !!!
    // -------------------------------------------------------------------------
    // Graphify output shape inferred best-effort. Verify against actual Graphify
    // output format and adjust mapping. May need to support both v1 and v2 schemas.
    //
    // Currently assumed top-level shape (defensive — tolerates either key set):
    //   {
    //     "schema_version": "1.0" | "schemaVersion": ...,
    //     "generated_at": "ISO-8601",
    //     "metadata": { "total_files": int, ... }   (optional)
    //     "nodes":     [ { "id", "type"/"label", "name", "file"/"file_path", ... } ],
    //     "edges" | "relationships" | "links":
    //                  [ { "source"/"from", "target"/"to", "type"/"label", ... } ]
    //   }
    //
    // If the real Graphify JSON differs (e.g., wraps in {"graph": {...}} or uses
    // capitalized keys), extend the lookups in TryGetProp() / ExtractNodes() /
    // ExtractEdges() below. Do NOT silently fail — throw GraphifyOutputMalformedException.
    // =========================================================================
    internal static GraphifyOutput ParseGraphifyOutput(string json, string outputPath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new GraphifyOutputMalformedException(
                $"Graphify output is not valid JSON ({outputPath}): {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new GraphifyOutputMalformedException(
                    $"Graphify output root must be a JSON object, got {root.ValueKind} ({outputPath})");
            }

            // Some Graphify versions may wrap content under a "graph" key.
            // Only use the nested object if it actually contains nodes — the real
            // graphify output uses "graph": {} (empty metadata dict) at root level
            // while nodes/links live directly on root.
            var graphEl = TryGetProp(root, "graph");
            var content = (graphEl is JsonElement g
                && g.ValueKind == JsonValueKind.Object
                && g.TryGetProperty("nodes", out _))
                ? g
                : root;

            var nodes = ExtractNodes(content);
            var edges = ExtractEdges(content);
            var metadata = ExtractMetadata(content, nodes.Count, edges.Count);

            return new GraphifyOutput(nodes, edges, metadata);
        }
    }

    private static List<GraphifyNode> ExtractNodes(JsonElement content)
    {
        var nodesEl = TryGetProp(content, "nodes")
            ?? TryGetProp(content, "vertices")
            ?? throw new GraphifyOutputMalformedException(
                "Graphify output missing 'nodes' (or 'vertices') array");

        if (nodesEl.ValueKind != JsonValueKind.Array)
            throw new GraphifyOutputMalformedException(
                $"Graphify 'nodes' must be an array, got {nodesEl.ValueKind}");

        var result = new List<GraphifyNode>(nodesEl.GetArrayLength());
        foreach (var n in nodesEl.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;

            var id = GetStringProp(n, "id") ?? GetStringProp(n, "_id");
            if (id is null)
                throw new GraphifyOutputMalformedException("Graphify node missing 'id'");

            var type = GetStringProp(n, "type")
                ?? GetStringProp(n, "label")
                ?? GetStringProp(n, "kind")
                ?? "unknown";
            var name = GetStringProp(n, "name") ?? GetStringProp(n, "title");
            var filePath = GetStringProp(n, "file_path")
                ?? GetStringProp(n, "filePath")
                ?? GetStringProp(n, "source_file")
                ?? GetStringProp(n, "sourceFile")
                ?? GetStringProp(n, "file")
                ?? GetStringProp(n, "path");

            var props = ExtractPropertyBag(n,
                excluded: new[] { "id", "_id", "type", "label", "kind", "name", "title",
                                  "file_path", "filePath", "source_file", "sourceFile",
                                  "file", "path" });

            result.Add(new GraphifyNode(id, type, name, filePath, props));
        }
        return result;
    }

    private static List<GraphifyEdge> ExtractEdges(JsonElement content)
    {
        var edgesEl = TryGetProp(content, "edges")
            ?? TryGetProp(content, "relationships")
            ?? TryGetProp(content, "links")
            ?? throw new GraphifyOutputMalformedException(
                "Graphify output missing 'edges' (or 'relationships'/'links') array");

        if (edgesEl.ValueKind != JsonValueKind.Array)
            throw new GraphifyOutputMalformedException(
                $"Graphify edges must be an array, got {edgesEl.ValueKind}");

        var result = new List<GraphifyEdge>(edgesEl.GetArrayLength());
        foreach (var e in edgesEl.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;

            var source = GetStringProp(e, "source")
                ?? GetStringProp(e, "from")
                ?? GetStringProp(e, "start")
                ?? throw new GraphifyOutputMalformedException("Graphify edge missing 'source'");
            var target = GetStringProp(e, "target")
                ?? GetStringProp(e, "to")
                ?? GetStringProp(e, "end")
                ?? throw new GraphifyOutputMalformedException("Graphify edge missing 'target'");
            var type = GetStringProp(e, "type")
                ?? GetStringProp(e, "relation")
                ?? GetStringProp(e, "label")
                ?? GetStringProp(e, "kind")
                ?? "unknown";

            var props = ExtractPropertyBag(e,
                excluded: new[] { "source", "from", "start", "target", "to", "end",
                                  "type", "relation", "label", "kind" });

            result.Add(new GraphifyEdge(source, target, type, props));
        }
        return result;
    }

    private static GraphifyMetadata ExtractMetadata(JsonElement content, int nodeCount, int edgeCount)
    {
        var metaEl = TryGetProp(content, "metadata");

        var schemaVersion =
            (metaEl is { } me ? GetStringProp(me, "schema_version") : null)
            ?? GetStringProp(content, "schema_version")
            ?? GetStringProp(content, "schemaVersion")
            ?? GetStringProp(content, "version")
            ?? "unknown";

        var generatedAtRaw =
            (metaEl is { } me2 ? GetStringProp(me2, "generated_at") : null)
            ?? GetStringProp(content, "generated_at")
            ?? GetStringProp(content, "generatedAt")
            ?? GetStringProp(content, "timestamp");
        var generatedAt = DateTimeOffset.TryParse(generatedAtRaw, out var ts) ? ts : DateTimeOffset.UtcNow;

        int totalFiles =
            (metaEl is { } me3 ? GetIntProp(me3, "total_files") : null)
            ?? GetIntProp(content, "total_files")
            ?? GetIntProp(content, "totalFiles")
            ?? 0;

        int totalNodes =
            (metaEl is { } me4 ? GetIntProp(me4, "total_nodes") : null)
            ?? GetIntProp(content, "total_nodes")
            ?? nodeCount;

        int totalEdges =
            (metaEl is { } me5 ? GetIntProp(me5, "total_edges") : null)
            ?? GetIntProp(content, "total_edges")
            ?? edgeCount;

        IReadOnlyDictionary<string, object?>? extras = null;
        if (metaEl is { } meX)
        {
            extras = ExtractPropertyBag(meX,
                excluded: new[] { "schema_version", "schemaVersion", "version",
                                  "generated_at", "generatedAt", "timestamp",
                                  "total_files", "totalFiles",
                                  "total_nodes", "totalNodes",
                                  "total_edges", "totalEdges" });
        }

        return new GraphifyMetadata(schemaVersion, generatedAt, totalFiles, totalNodes, totalEdges, extras);
    }

    private static JsonElement? TryGetProp(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        return obj.TryGetProperty(name, out var v) ? v : null;
    }

    private static string? GetStringProp(JsonElement obj, string name)
    {
        var p = TryGetProp(obj, name);
        if (p is null) return null;
        return p.Value.ValueKind switch
        {
            JsonValueKind.String => p.Value.GetString(),
            JsonValueKind.Number => p.Value.ToString(),
            _ => null,
        };
    }

    private static int? GetIntProp(JsonElement obj, string name)
    {
        var p = TryGetProp(obj, name);
        if (p is null) return null;
        return p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i) ? i : null;
    }

    private static IReadOnlyDictionary<string, object?> ExtractPropertyBag(
        JsonElement obj, IReadOnlyCollection<string> excluded)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (obj.ValueKind != JsonValueKind.Object) return dict;

        foreach (var prop in obj.EnumerateObject())
        {
            if (excluded.Contains(prop.Name)) continue;
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }
        return dict;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal),
        _ => el.GetRawText(),
    };

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have exited between the check and Kill; nothing actionable.
        }
    }
}
