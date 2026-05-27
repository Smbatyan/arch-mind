using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Cloning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Graphify;

/// <summary>
/// Reads <c>combined_graph.json</c> produced by <c>combine_workspace_graph.py</c>.
/// Caches the parsed graph in memory per workspace for <see cref="CacheTtl"/>.
///
/// File format (from networkx.readwrite.json_graph.node_link_data via graphify
/// to_json):
/// <code>
/// {
///   "nodes": [{ "id", "label", "name?", "repo_id?", "source_file?", ... }],
///   "links": [{ "source", "target", "relation"?, "label"? }]
/// }
/// </code>
/// </summary>
public sealed class StructuralGraphService : IStructuralGraphService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private sealed record CachedGraph(
        DateTime ExpiresAt,
        IReadOnlyList<NodeRecord> Nodes,
        IReadOnlyList<EdgeRecord> Edges,
        IReadOnlyDictionary<string, int> Degrees);

    private sealed record NodeRecord(
        string Id,
        string Label,
        string Name,
        string? RepoId,
        string? SourceFile);

    private sealed record EdgeRecord(string Source, string Target, string Label);

    private readonly ConcurrentDictionary<Guid, CachedGraph> _cache = new();
    private readonly CloningOptions _cloning;
    private readonly ILogger<StructuralGraphService> _logger;

    public StructuralGraphService(
        IOptions<CloningOptions> cloning,
        ILogger<StructuralGraphService> logger)
    {
        _cloning = cloning.Value;
        _logger = logger;
    }

    public async Task<StructuralGraphData> GetAsync(
        Guid workspaceId,
        Guid? repoId,
        int limit,
        CancellationToken ct = default)
    {
        var cached = await LoadAsync(workspaceId, ct);
        if (cached is null)
        {
            return new StructuralGraphData(
                Array.Empty<StructuralGraphNode>(),
                Array.Empty<StructuralGraphEdge>(),
                TotalNodes: 0,
                TotalEdges: 0,
                Truncated: false);
        }

        // Optional repo filter (matches the UUID prefix stored in NodeRecord.RepoId).
        var nodesAfterFilter = repoId.HasValue
            ? cached.Nodes.Where(n => string.Equals(n.RepoId, repoId.Value.ToString(), StringComparison.OrdinalIgnoreCase)).ToList()
            : cached.Nodes.ToList();

        var totalAfterFilter = nodesAfterFilter.Count;
        var truncated = totalAfterFilter > limit;

        // Keep highest-degree nodes (most "interesting" from a topology POV).
        var topNodes = nodesAfterFilter
            .OrderByDescending(n => cached.Degrees.GetValueOrDefault(n.Id, 0))
            .Take(limit)
            .ToList();

        var idSet = new HashSet<string>(topNodes.Select(n => n.Id), StringComparer.Ordinal);

        var edgeDtos = cached.Edges
            .Where(e => idSet.Contains(e.Source) && idSet.Contains(e.Target))
            .Select((e, i) => new StructuralGraphEdge(
                $"e{i}",
                e.Source,
                e.Target,
                string.IsNullOrEmpty(e.Label) ? "rel" : e.Label))
            .ToList();

        var nodeDtos = topNodes
            .Select(n => new StructuralGraphNode(
                n.Id,
                n.Label,
                n.Name,
                n.RepoId,
                n.SourceFile,
                cached.Degrees.GetValueOrDefault(n.Id, 0)))
            .ToList();

        return new StructuralGraphData(nodeDtos, edgeDtos, totalAfterFilter, cached.Edges.Count, truncated);
    }

    public async Task<IReadOnlyList<string>> SearchByKeywordsAsync(
        Guid workspaceId,
        IReadOnlyList<string> keywords,
        int limit,
        CancellationToken ct = default)
    {
        var cached = await LoadAsync(workspaceId, ct);
        if (cached is null || keywords.Count == 0)
            return Array.Empty<string>();

        var needles = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        if (needles.Count == 0)
            return Array.Empty<string>();

        // Score = number of needles hit + degree weighting tiebreak.
        var scored = new List<(string Id, int Score, int Degree)>(capacity: 256);
        foreach (var n in cached.Nodes)
        {
            var haystack = (n.Name + " " + n.Label + " " + (n.SourceFile ?? "")).ToLowerInvariant();
            var hits = needles.Count(k => haystack.Contains(k));
            if (hits > 0)
            {
                scored.Add((n.Id, hits, cached.Degrees.GetValueOrDefault(n.Id, 0)));
            }
        }

        return scored
            .OrderByDescending(t => t.Score)
            .ThenByDescending(t => t.Degree)
            .Take(limit)
            .Select(t => t.Id)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Internal: load + parse + cache
    // -------------------------------------------------------------------------
    private async Task<CachedGraph?> LoadAsync(Guid workspaceId, CancellationToken ct)
    {
        if (_cache.TryGetValue(workspaceId, out var hit) && DateTime.UtcNow < hit.ExpiresAt)
            return hit;

        var path = Path.Combine(
            _cloning.WorkingDirRoot,
            workspaceId.ToString(),
            "graphify-out",
            "combined_graph.json");

        if (!File.Exists(path))
        {
            _logger.LogDebug(
                "StructuralGraphService: combined_graph.json not found at {Path} workspace={WorkspaceId}",
                path, workspaceId);
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var parsed = await JsonSerializer.DeserializeAsync<CombinedGraphFile>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString,
                },
                ct);

            if (parsed is null)
                return null;

            var nodes = new List<NodeRecord>(parsed.Nodes.Count);
            foreach (var n in parsed.Nodes)
            {
                var id = n.Id ?? string.Empty;
                if (string.IsNullOrEmpty(id))
                    continue;

                nodes.Add(new NodeRecord(
                    Id: id,
                    Label: n.FileType ?? "code",
                    Name: n.Label ?? id,
                    RepoId: n.RepoId,
                    SourceFile: n.SourceFile));
            }

            var edges = new List<EdgeRecord>(parsed.Links.Count);
            foreach (var e in parsed.Links)
            {
                if (string.IsNullOrEmpty(e.Source) || string.IsNullOrEmpty(e.Target))
                    continue;
                edges.Add(new EdgeRecord(e.Source!, e.Target!, e.Relation ?? e.Label ?? "rel"));
            }

            var degrees = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in edges)
            {
                degrees[e.Source] = degrees.GetValueOrDefault(e.Source) + 1;
                degrees[e.Target] = degrees.GetValueOrDefault(e.Target) + 1;
            }

            var cached = new CachedGraph(DateTime.UtcNow.Add(CacheTtl), nodes, edges, degrees);
            _cache[workspaceId] = cached;

            _logger.LogInformation(
                "StructuralGraphService: loaded combined_graph.json workspace={WorkspaceId} nodes={Nodes} edges={Edges}",
                workspaceId, nodes.Count, edges.Count);

            return cached;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "StructuralGraphService: failed to parse {Path} workspace={WorkspaceId}",
                path, workspaceId);
            return null;
        }
    }

    /// <summary>
    /// Drop cached graph for a workspace; called after a successful workspace
    /// graph rebuild so the next request reflects new repos.
    /// </summary>
    public void Invalidate(Guid workspaceId)
    {
        _cache.TryRemove(workspaceId, out _);
    }

    // -------------------------------------------------------------------------
    // JSON shape mirrors networkx node_link_data with the extras the combine
    // script writes onto each node.
    // -------------------------------------------------------------------------
    private sealed class CombinedGraphFile
    {
        [JsonPropertyName("nodes")]
        public List<RawNode> Nodes { get; set; } = new();

        [JsonPropertyName("links")]
        public List<RawEdge> Links { get; set; } = new();
    }

    private sealed class RawNode
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("file_type")] public string? FileType { get; set; }
        [JsonPropertyName("source_file")] public string? SourceFile { get; set; }
        [JsonPropertyName("repo")] public string? Repo { get; set; }
        [JsonPropertyName("repo_id")] public string? RepoId { get; set; }
    }

    private sealed class RawEdge
    {
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("relation")] public string? Relation { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
    }
}
