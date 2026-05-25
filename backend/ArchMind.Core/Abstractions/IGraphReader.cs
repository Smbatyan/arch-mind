using ArchMind.Core.Models.Graph;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// Read-only accessor for the Apache AGE <c>archmind_graph</c>. All queries are
/// workspace-scoped: every Cypher matcher must include
/// <c>{workspace_id: $ws}</c> and implementations MUST reject
/// <see cref="Guid.Empty"/> to prevent cross-tenant leakage.
///
/// Owns reads only. Writes live behind <c>IGraphWriter</c> (BE-021).
/// </summary>
public interface IGraphReader
{
    Task<ServiceNode?> GetServiceAsync(Guid workspaceId, string name, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceNode>> ListServicesAsync(Guid workspaceId, CancellationToken ct = default);

    Task<IReadOnlyList<EndpointNode>> GetServiceEndpointsAsync(Guid workspaceId, string serviceName, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceNode>> GetEventPublishersAsync(Guid workspaceId, string eventName, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceNode>> GetEventConsumersAsync(Guid workspaceId, string eventName, CancellationToken ct = default);

    Task<TopologyResult> GetTopologyAsync(Guid workspaceId, CancellationToken ct = default);

    Task<IReadOnlyList<DependencyNode>> GetServiceDependenciesAsync(Guid workspaceId, string serviceName, CancellationToken ct = default);

    Task<IReadOnlyList<ConventionNode>> GetConventionsAsync(Guid workspaceId, string? category = null, CancellationToken ct = default);

    Task<IReadOnlyList<NodeSummary>> ListNodesByLabelAsync(Guid workspaceId, string label, int limit = 200, CancellationToken ct = default);

    Task<NodeDetail?> GetNodeAsync(Guid workspaceId, string label, Guid nodeId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> CountNodesPerLabelAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Returns workspace-wide node and edge counts grouped by label. Backs the
    /// <c>graph/overview</c> MCP resource. Cheap enough to compute on demand
    /// for graphs up to ~50K nodes.
    /// </summary>
    Task<Core.Models.Graph.GraphOverview> GetOverviewAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// 1-hop neighborhood for a Service node: endpoints (EXPOSES), dependencies
    /// (DEPENDS_ON / READS), and published/consumed events. Returns null when
    /// no Service with that name exists in the workspace.
    /// </summary>
    Task<Core.Models.Graph.ServiceNeighborhood?> GetServiceNeighborhoodAsync(
        Guid workspaceId, string serviceName, CancellationToken ct = default);

    /// <summary>
    /// All Endpoint nodes in the workspace, optionally filtered by owning
    /// service name and / or HTTP method.
    /// </summary>
    Task<IReadOnlyList<Core.Models.Graph.EndpointNode>> ListEndpointsAsync(
        Guid workspaceId,
        string? serviceName,
        string? method,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the callers of an Endpoint identified by node id, traversing
    /// incoming CALLS edges.
    /// </summary>
    Task<IReadOnlyList<Core.Models.Graph.EndpointCaller>> FindEndpointCallersByIdAsync(
        Guid workspaceId, Guid endpointId, CancellationToken ct = default);

    /// <summary>
    /// Returns the callers of an Endpoint identified by (method, path).
    /// </summary>
    Task<IReadOnlyList<Core.Models.Graph.EndpointCaller>> FindEndpointCallersByRouteAsync(
        Guid workspaceId, string method, string path, CancellationToken ct = default);

    /// <summary>
    /// BE-031: case-insensitive text search across every workspace-scoped vertex.
    /// A node matches if any of the supplied <paramref name="tokens"/> appears
    /// as a substring in its <c>name</c> or <c>description</c> property. Results
    /// are capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<Core.Models.Graph.NodeSearchHit>> SearchNodesByTextAsync(
        Guid workspaceId,
        IEnumerable<string> tokens,
        int limit,
        CancellationToken ct = default);
}
