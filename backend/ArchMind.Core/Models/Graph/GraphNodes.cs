namespace ArchMind.Core.Models.Graph;

/// <summary>
/// Result records exposed by <see cref="ArchMind.Core.Abstractions.IGraphReader"/>.
///
/// These are plain DTOs deliberately decoupled from any persistence detail
/// (Apache AGE vertex/edge agtype representation lives inside the
/// infrastructure-layer reader). All references to nodes use the canonical
/// <c>node_id</c> Guid stored as a property — never AGE's synthetic
/// <c>id(n)</c> bigint, which is per-graph-internal and not stable across
/// rebuilds.
/// </summary>
public sealed record ServiceNode(
    Guid Id,
    string Name,
    string? Purpose,
    Guid? RepoId,
    string? RootPath,
    IReadOnlyList<string> TechStack);

public sealed record EndpointNode(
    Guid Id,
    string Method,
    string Path,
    string? HandlerFile);

public sealed record DependencyNode(
    Guid Id,
    string Name,
    string Type,
    string? Version);

public sealed record ConventionNode(
    Guid Id,
    string Category,
    string Name,
    string Description);

public sealed record NodeSummary(
    Guid Id,
    string Label,
    string? Name);

public sealed record EdgeRef(
    string Label,
    Guid OtherNodeId,
    string OtherNodeLabel,
    string? OtherNodeName,
    IReadOnlyDictionary<string, object?> Properties);

public sealed record NodeDetail(
    Guid Id,
    string Label,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyList<EdgeRef> IncomingEdges,
    IReadOnlyList<EdgeRef> OutgoingEdges);

public sealed record EventRef(Guid Id, string Name);

public sealed record TopologyEdge(string Label, Guid SourceId, Guid TargetId);

public sealed record TopologyResult(
    IReadOnlyList<ServiceNode> Services,
    IReadOnlyList<EventRef> Events,
    IReadOnlyList<TopologyEdge> Edges);

/// <summary>
/// Aggregated counts of nodes and edges per label for a workspace. Exposed as
/// an MCP resource (BE-029) and useful as a quick sanity-check that the graph
/// reflects what the orchestrator produced.
/// </summary>
public sealed record GraphOverview(
    IReadOnlyDictionary<string, int> NodeCounts,
    IReadOnlyDictionary<string, int> EdgeCounts);

/// <summary>
/// 1-hop neighborhood around a Service node — its outgoing endpoints,
/// dependencies, and published/consumed events. Used by the MCP
/// <c>get_microservice</c> tool.
/// </summary>
public sealed record ServiceNeighborhood(
    ServiceNode Service,
    IReadOnlyList<EndpointNode> Endpoints,
    IReadOnlyList<DependencyNode> Dependencies,
    IReadOnlyList<EventRef> Publishes,
    IReadOnlyList<EventRef> Consumes);

/// <summary>
/// Caller of an Endpoint — typically a Service that has a CALLS edge into the
/// endpoint. <see cref="CallerName"/> may be null for headless callers.
/// </summary>
public sealed record EndpointCaller(
    Guid CallerId,
    string CallerLabel,
    string? CallerName);

/// <summary>
/// BE-031: one hit returned by <see cref="ArchMind.Core.Abstractions.IGraphReader.SearchNodesByTextAsync"/>.
/// <see cref="Properties"/> is the raw vertex property bag (Guids surfaced as
/// strings) so callers can render whatever fields they care about without a
/// second lookup.
/// </summary>
public sealed record NodeSearchHit(
    Guid Id,
    string Label,
    string? Name,
    string? Description,
    IReadOnlyDictionary<string, object?> Properties);

// ── Graph visualization data ─────────────────────────────────────────────────

public sealed record VisualizationNode(Guid Id, string Label, string? Name, Guid? RepoId = null);

public sealed record VisualizationEdge(Guid SourceId, Guid TargetId, string Label);

/// <summary>
/// Flat node+edge payload for the visual graph canvas.
/// <see cref="Truncated"/> is true when the node cap was hit.
/// </summary>
public sealed record VisualizationData(
    IReadOnlyList<VisualizationNode> Nodes,
    IReadOnlyList<VisualizationEdge> Edges,
    bool Truncated);
