using System.Collections.Concurrent;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Cloning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Graphify;

/// <summary>
/// Singleton service that reads the on-disk <c>graphify-out/graph.json</c>
/// produced by <see cref="GraphifyRunner"/> and returns per-file structural
/// context for use in LLM extraction prompts.
///
/// Graph files are loaded lazily per (workspaceId, repoId) pair and cached
/// in memory for <see cref="CacheTtl"/> to amortise I/O across the many
/// per-file <c>LlmExtractionJob</c> calls that follow a single scan.
///
/// All failures (file not found, parse error) are swallowed and logged so that
/// missing Graphify context never blocks LLM extraction.
/// </summary>
public sealed class GraphifyContextService : IGraphifyContextService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private sealed record CachedGraph(
        DateTime ExpiresAt,
        GraphifyOutput Output,
        IReadOnlyDictionary<string, IReadOnlyList<GraphifyNode>> NodesByFile,
        IReadOnlyDictionary<string, IReadOnlyList<GraphifyEdge>> OutboundByNodeId,
        IReadOnlyDictionary<string, IReadOnlyList<GraphifyEdge>> InboundByNodeId);

    private readonly ConcurrentDictionary<(Guid WorkspaceId, Guid RepoId), CachedGraph> _cache = new();
    private readonly CloningOptions _cloning;
    private readonly GraphifyOptions _graphify;
    private readonly ILogger<GraphifyContextService> _logger;

    public GraphifyContextService(
        IOptions<CloningOptions> cloning,
        IOptions<GraphifyOptions> graphify,
        ILogger<GraphifyContextService> logger)
    {
        _cloning = cloning.Value;
        _graphify = graphify.Value;
        _logger = logger;
    }

    public async Task<GraphifyFileContext> GetFileContextAsync(
        Guid workspaceId,
        Guid repoId,
        string filePath,
        CancellationToken ct = default)
    {
        var cacheKey = (workspaceId, repoId);

        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow < hit.ExpiresAt)
        {
            return BuildFileContext(hit, filePath);
        }

        var graphPath = Path.Combine(
            _cloning.WorkingDirRoot,
            workspaceId.ToString(),
            "repos",
            repoId.ToString(),
            _graphify.OutputSubdirectory,
            _graphify.OutputFileName);

        if (!File.Exists(graphPath))
        {
            _logger.LogDebug(
                "Graphify output not found at {Path}; no structural context for workspace={WorkspaceId} repo={RepoId}",
                graphPath, workspaceId, repoId);
            return GraphifyFileContext.Empty;
        }

        try
        {
            var json = await File.ReadAllTextAsync(graphPath, ct);
            var output = GraphifyRunner.ParseGraphifyOutput(json, graphPath);
            var cached = BuildCache(output);
            _cache[cacheKey] = cached;
            return BuildFileContext(cached, filePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load graphify context from {Path}; LLM extraction will proceed without structural context workspace={WorkspaceId} repo={RepoId}",
                graphPath, workspaceId, repoId);
            return GraphifyFileContext.Empty;
        }
    }

    public async Task<GraphifyOutput?> GetRepoGraphAsync(
        Guid workspaceId,
        Guid repoId,
        CancellationToken ct = default)
    {
        var cacheKey = (workspaceId, repoId);

        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow < hit.ExpiresAt)
        {
            return hit.Output;
        }

        var graphPath = Path.Combine(
            _cloning.WorkingDirRoot,
            workspaceId.ToString(),
            "repos",
            repoId.ToString(),
            _graphify.OutputSubdirectory,
            _graphify.OutputFileName);

        if (!File.Exists(graphPath))
        {
            _logger.LogDebug(
                "Graphify output not found at {Path}; no structural context for workspace={WorkspaceId} repo={RepoId}",
                graphPath, workspaceId, repoId);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(graphPath, ct);
            var output = GraphifyRunner.ParseGraphifyOutput(json, graphPath);
            var cached = BuildCache(output);
            _cache[cacheKey] = cached;
            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load graphify context from {Path}; repo-level DEPENDS_ON edges will be skipped workspace={WorkspaceId} repo={RepoId}",
                graphPath, workspaceId, repoId);
            return null;
        }
    }

    private static CachedGraph BuildCache(GraphifyOutput output)
    {
        var nodesByFile = new Dictionary<string, List<GraphifyNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in output.Nodes)
        {
            if (string.IsNullOrEmpty(node.FilePath))
                continue;

            var key = NormalisePath(node.FilePath);
            if (!nodesByFile.TryGetValue(key, out var list))
                nodesByFile[key] = list = new List<GraphifyNode>();
            list.Add(node);
        }

        var outbound = new Dictionary<string, List<GraphifyEdge>>(StringComparer.Ordinal);
        var inbound = new Dictionary<string, List<GraphifyEdge>>(StringComparer.Ordinal);
        foreach (var edge in output.Edges)
        {
            if (!outbound.TryGetValue(edge.Source, out var outList))
                outbound[edge.Source] = outList = new List<GraphifyEdge>();
            outList.Add(edge);

            if (!inbound.TryGetValue(edge.Target, out var inList))
                inbound[edge.Target] = inList = new List<GraphifyEdge>();
            inList.Add(edge);
        }

        return new CachedGraph(
            DateTime.UtcNow.Add(CacheTtl),
            output,
            nodesByFile.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<GraphifyNode>)kvp.Value.AsReadOnly(),
                StringComparer.OrdinalIgnoreCase),
            outbound.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<GraphifyEdge>)kvp.Value.AsReadOnly(),
                StringComparer.Ordinal),
            inbound.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<GraphifyEdge>)kvp.Value.AsReadOnly(),
                StringComparer.Ordinal));
    }

    private static GraphifyFileContext BuildFileContext(CachedGraph cached, string filePath)
    {
        var key = NormalisePath(filePath);
        if (!cached.NodesByFile.TryGetValue(key, out var nodes) || nodes.Count == 0)
            return GraphifyFileContext.Empty;

        var nodeIds = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.Ordinal);

        var outboundEdges = nodeIds
            .SelectMany(id => cached.OutboundByNodeId.TryGetValue(id, out var e)
                ? e : Enumerable.Empty<GraphifyEdge>())
            .ToList();

        var inboundEdges = nodeIds
            .SelectMany(id => cached.InboundByNodeId.TryGetValue(id, out var e)
                ? e : Enumerable.Empty<GraphifyEdge>())
            .ToList();

        return new GraphifyFileContext(nodes, outboundEdges, inboundEdges);
    }

    private static string NormalisePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
