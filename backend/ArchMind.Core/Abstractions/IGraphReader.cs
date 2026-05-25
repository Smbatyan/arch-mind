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
}
