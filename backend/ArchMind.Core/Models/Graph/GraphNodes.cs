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
