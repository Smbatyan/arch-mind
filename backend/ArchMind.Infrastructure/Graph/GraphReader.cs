using System.Data.Common;
using System.Text.Json;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Models.Graph;
using Dapper;

namespace ArchMind.Infrastructure.Graph;

/// <summary>
/// Read-only Apache AGE accessor. Each public method opens a fresh connection
/// via <see cref="IDbConnectionFactory"/> (which has already executed
/// <c>LOAD 'age'</c> + <c>SET search_path</c>), issues a single Cypher
/// statement wrapped in <c>SELECT * FROM cypher(...)</c>, and parses the
/// resulting <c>agtype</c> strings via <see cref="AgtypeParser"/>.
///
/// Workspace scoping is enforced at the C# layer: every entry-point validates
/// that <c>workspaceId != Guid.Empty</c> via <see cref="EnsureScoped"/>, and
/// every Cypher MATCH binds <c>workspace_id: $ws</c>.
///
/// Cypher parameters are passed via AGE's third <c>cypher()</c> argument as a
/// JSON object cast to <c>agtype</c>. Two Postgres positional parameters are
/// used: <c>$1</c> = graph name (constant), <c>$2</c> = JSON params. Dapper
/// supplies these as <c>@graph</c> and <c>@params</c>; we generate the SQL
/// with explicit casts.
/// </summary>
internal sealed class GraphReader : IGraphReader
{
    private const string GraphName = "archmind_graph";

    private readonly IDbConnectionFactory _factory;

    public GraphReader(IDbConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    // ── Public API ────────────────────────────────────────────────────────

    // PERF: hits idx_service_workspace_name (BE-020); single point lookup
    // expected <10ms on 10K-node graph.
    public async Task<ServiceNode?> GetServiceAsync(Guid workspaceId, string name, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string cypher = """
            MATCH (s:Service {workspace_id: $ws, name: $name})
            RETURN s LIMIT 1
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var row = await QuerySingleColumnAsync(conn, cypher, new { ws = workspaceId, name }, "s", ct)
            .ConfigureAwait(false);

        return row is null ? null : MapService(row);
    }

    // PERF: scans Service partition by workspace_id (idx_service_workspace).
    // Up to ~hundreds of services per workspace expected; ORDER BY name is
    // in-memory and trivial at this scale.
    public async Task<IReadOnlyList<ServiceNode>> ListServicesAsync(Guid workspaceId, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);

        const string cypher = """
            MATCH (s:Service {workspace_id: $ws})
            RETURN s
            ORDER BY s.name
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher, new { ws = workspaceId }, "s", ct)
            .ConfigureAwait(false);

        return rows.Select(MapService).ToList();
    }

    // PERF: anchor lookup via idx_service_workspace_name then EXPOSES traversal.
    // Endpoints per service typically <100 → traversal is bounded.
    public async Task<IReadOnlyList<EndpointNode>> GetServiceEndpointsAsync(
        Guid workspaceId, string serviceName, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        const string cypher = """
            MATCH (s:Service {workspace_id: $ws, name: $name})-[:EXPOSES]->(e:Endpoint)
            RETURN e
            ORDER BY e.path, e.method
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher, new { ws = workspaceId, name = serviceName }, "e", ct)
            .ConfigureAwait(false);

        return rows.Select(MapEndpoint).ToList();
    }

    // PERF: anchor on Event (idx_event_workspace_name) then back-walk to Service.
    public async Task<IReadOnlyList<ServiceNode>> GetEventPublishersAsync(
        Guid workspaceId, string eventName, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        const string cypher = """
            MATCH (s:Service {workspace_id: $ws})-[:PUBLISHES]->(e:Event {workspace_id: $ws, name: $name})
            RETURN s
            ORDER BY s.name
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher, new { ws = workspaceId, name = eventName }, "s", ct)
            .ConfigureAwait(false);

        return rows.Select(MapService).ToList();
    }

    // PERF: same as GetEventPublishersAsync but edge label CONSUMES.
    public async Task<IReadOnlyList<ServiceNode>> GetEventConsumersAsync(
        Guid workspaceId, string eventName, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        const string cypher = """
            MATCH (s:Service {workspace_id: $ws})-[:CONSUMES]->(e:Event {workspace_id: $ws, name: $name})
            RETURN s
            ORDER BY s.name
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher, new { ws = workspaceId, name = eventName }, "s", ct)
            .ConfigureAwait(false);

        return rows.Select(MapService).ToList();
    }

    // PERF: three full-partition scans (services, events, pub/con edges) but
    // every match is workspace_id-anchored. For 10K-node graphs the result set
    // is small (~hundreds of vertices). Combining in-process avoids a single
    // mega-Cypher with cartesian risks.
    public async Task<TopologyResult> GetTopologyAsync(Guid workspaceId, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);

        const string servicesCypher = """
            MATCH (s:Service {workspace_id: $ws})
            RETURN s
            ORDER BY s.name
        """;

        const string eventsCypher = """
            MATCH (e:Event {workspace_id: $ws})
            RETURN e
            ORDER BY e.name
        """;

        // We need source service id, target event id, and edge label. RETURN
        // the three pieces as three columns so the parser doesn't have to walk
        // the edge's start_id/end_id (which are AGE-internal bigints, not our
        // domain Guids).
        const string edgesCypher = """
            MATCH (s:Service {workspace_id: $ws})-[r:PUBLISHES|CONSUMES]->(e:Event {workspace_id: $ws})
            RETURN type(r) AS rel, s.id AS source_id, e.id AS target_id
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var serviceRows = await QueryColumnAsync(conn, servicesCypher, new { ws = workspaceId }, "s", ct)
            .ConfigureAwait(false);
        var eventRows = await QueryColumnAsync(conn, eventsCypher, new { ws = workspaceId }, "e", ct)
            .ConfigureAwait(false);
        var edgeRows = await QueryMultiColumnAsync(
            conn, edgesCypher, new { ws = workspaceId },
            new[] { "rel", "source_id", "target_id" }, ct).ConfigureAwait(false);

        var services = serviceRows.Select(MapService).ToList();
        var events = eventRows.Select(MapEventRef).ToList();

        var edges = new List<TopologyEdge>(edgeRows.Count);
        foreach (var row in edgeRows)
        {
            var label = UnquoteAgtypeString(row[0]) ?? string.Empty;
            var sourceId = ParseGuidScalar(row[1]);
            var targetId = ParseGuidScalar(row[2]);
            if (sourceId is null || targetId is null) continue;
            edges.Add(new TopologyEdge(label, sourceId.Value, targetId.Value));
        }

        return new TopologyResult(services, events, edges);
    }

    // PERF: anchor on Service then DEPENDS_ON traversal. Index on
    // Service(workspace_id, name).
    public async Task<IReadOnlyList<DependencyNode>> GetServiceDependenciesAsync(
        Guid workspaceId, string serviceName, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        const string cypher = """
            MATCH (s:Service {workspace_id: $ws, name: $name})-[:DEPENDS_ON]->(d:Dependency)
            RETURN d
            ORDER BY d.name
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher, new { ws = workspaceId, name = serviceName }, "d", ct)
            .ConfigureAwait(false);

        return rows.Select(MapDependency).ToList();
    }

    // PERF: idx_convention_workspace; optional category filter is in-Cypher to
    // avoid pulling rows we'd discard client-side.
    public async Task<IReadOnlyList<ConventionNode>> GetConventionsAsync(
        Guid workspaceId, string? category = null, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);

        var cypher = category is null
            ? """
                MATCH (c:Convention {workspace_id: $ws})
                RETURN c
                ORDER BY c.category, c.name
            """
            : """
                MATCH (c:Convention {workspace_id: $ws, category: $cat})
                RETURN c
                ORDER BY c.name
            """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        object parameters = category is null
            ? new { ws = workspaceId }
            : new { ws = workspaceId, cat = category };

        var rows = await QueryColumnAsync(conn, cypher, parameters, "c", ct).ConfigureAwait(false);
        return rows.Select(MapConvention).ToList();
    }

    // PERF: per-label workspace partition scan. Label is interpolated into the
    // Cypher (AGE has no label parameterisation) — validated against
    // GraphLabels.Vertex to prevent injection. Limit defaults to 200.
    public async Task<IReadOnlyList<NodeSummary>> ListNodesByLabelAsync(
        Guid workspaceId, string label, int limit = 200, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        EnsureKnownVertexLabel(label);
        if (limit <= 0) limit = 200;
        if (limit > 5000) limit = 5000;

        // Label is allowlisted above → safe to interpolate. Limit is forced
        // to a sane int range → safe to interpolate. WS comes via params.
        var cypher = $$"""
            MATCH (n:{{label}} {workspace_id: $ws})
            RETURN n.id AS id, n.name AS name
            LIMIT {{limit}}
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryMultiColumnAsync(
            conn, cypher, new { ws = workspaceId },
            new[] { "id", "name" }, ct).ConfigureAwait(false);

        var summaries = new List<NodeSummary>(rows.Count);
        foreach (var row in rows)
        {
            var id = ParseGuidScalar(row[0]);
            if (id is null) continue;
            summaries.Add(new NodeSummary(id.Value, label, UnquoteAgtypeString(row[1])));
        }
        return summaries;
    }

    // PERF: 3 Cypher queries — node by (label, id), incoming edges, outgoing
    // edges. Each is index-anchored. Total budget <50ms typical.
    public async Task<NodeDetail?> GetNodeAsync(
        Guid workspaceId, string label, Guid nodeId, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        EnsureKnownVertexLabel(label);
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("nodeId must be non-empty.", nameof(nodeId));
        }

        var nodeCypher = $$"""
            MATCH (n:{{label}} {workspace_id: $ws, id: $nid})
            RETURN n LIMIT 1
        """;

        // We return the OTHER side of the edge plus the edge so we can build
        // EdgeRef without a separate lookup.
        var incomingCypher = $$"""
            MATCH (other)-[r]->(n:{{label}} {workspace_id: $ws, id: $nid})
            RETURN type(r) AS rel, other, r
        """;

        var outgoingCypher = $$"""
            MATCH (n:{{label}} {workspace_id: $ws, id: $nid})-[r]->(other)
            RETURN type(r) AS rel, other, r
        """;

        var parameters = new { ws = workspaceId, nid = nodeId };

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var nodeRaw = await QuerySingleColumnAsync(conn, nodeCypher, parameters, "n", ct)
            .ConfigureAwait(false);

        if (nodeRaw is null) return null;

        var properties = AgtypeParser.ExtractProperties(nodeRaw);
        var id = TryReadGuidProperty(properties, "id") ?? nodeId;

        var incomingRows = await QueryMultiColumnAsync(
            conn, incomingCypher, parameters,
            new[] { "rel", "other", "r" }, ct).ConfigureAwait(false);
        var outgoingRows = await QueryMultiColumnAsync(
            conn, outgoingCypher, parameters,
            new[] { "rel", "other", "r" }, ct).ConfigureAwait(false);

        var incoming = incomingRows.Select(MapEdgeRow).Where(e => e is not null).Cast<EdgeRef>().ToList();
        var outgoing = outgoingRows.Select(MapEdgeRow).Where(e => e is not null).Cast<EdgeRef>().ToList();

        return new NodeDetail(id, label, properties, incoming, outgoing);
    }

    // PERF: N small COUNT queries (one per known label). Acceptable for the
    // MVP — each is a partition-by-workspace_id scan that lives in shared
    // buffers after the first warm-up. If this becomes a hotspot we can move
    // to a single UNION query, but the per-label form is easier to debug.
    public async Task<IReadOnlyDictionary<string, int>> CountNodesPerLabelAsync(
        Guid workspaceId, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);

        var result = new Dictionary<string, int>(GraphLabels.Vertex.Count, StringComparer.Ordinal);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        foreach (var label in GraphLabels.Vertex)
        {
            // Label safe — comes from internal allowlist.
            var cypher = $$"""
                MATCH (n:{{label}} {workspace_id: $ws})
                RETURN count(n) AS cnt
            """;

            var raw = await QuerySingleColumnAsync(
                conn, cypher, new { ws = workspaceId }, "cnt", ct).ConfigureAwait(false);

            result[label] = ParseIntScalar(raw) ?? 0;
        }

        return result;
    }

    // PERF: workspace-wide aggregations — one Cypher per label/edge. The
    // alternative (single MATCH(n) RETURN labels(n)[0]) would not benefit from
    // our per-label indexes and would scan every partition.
    public async Task<GraphOverview> GetOverviewAsync(Guid workspaceId, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);

        var nodeCounts = await CountNodesPerLabelAsync(workspaceId, ct).ConfigureAwait(false);

        var edgeCounts = new Dictionary<string, int>(GraphLabels.Edge.Count, StringComparer.Ordinal);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        foreach (var edge in GraphLabels.Edge)
        {
            // Edge label is allowlisted → safe to interpolate. We scope by
            // workspace via the endpoints of the edge (both must be in-tenant).
            var cypher = $$"""
                MATCH (a {workspace_id: $ws})-[r:{{edge}}]->(b {workspace_id: $ws})
                RETURN count(r) AS cnt
            """;

            var raw = await QuerySingleColumnAsync(
                conn, cypher, new { ws = workspaceId }, "cnt", ct).ConfigureAwait(false);

            edgeCounts[edge] = ParseIntScalar(raw) ?? 0;
        }

        return new GraphOverview(nodeCounts, edgeCounts);
    }

    public async Task<ServiceNeighborhood?> GetServiceNeighborhoodAsync(
        Guid workspaceId, string serviceName, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var service = await GetServiceAsync(workspaceId, serviceName, ct).ConfigureAwait(false);
        if (service is null) return null;

        var endpoints = await GetServiceEndpointsAsync(workspaceId, serviceName, ct).ConfigureAwait(false);
        var dependencies = await GetServiceDependenciesAsync(workspaceId, serviceName, ct).ConfigureAwait(false);

        const string publishesCypher = """
            MATCH (s:Service {workspace_id: $ws, name: $name})-[:PUBLISHES]->(e:Event {workspace_id: $ws})
            RETURN e
            ORDER BY e.name
        """;

        const string consumesCypher = """
            MATCH (s:Service {workspace_id: $ws, name: $name})-[:CONSUMES]->(e:Event {workspace_id: $ws})
            RETURN e
            ORDER BY e.name
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var pubRows = await QueryColumnAsync(conn, publishesCypher,
            new { ws = workspaceId, name = serviceName }, "e", ct).ConfigureAwait(false);
        var conRows = await QueryColumnAsync(conn, consumesCypher,
            new { ws = workspaceId, name = serviceName }, "e", ct).ConfigureAwait(false);

        return new ServiceNeighborhood(
            service,
            endpoints,
            dependencies,
            pubRows.Select(MapEventRef).ToList(),
            conRows.Select(MapEventRef).ToList());
    }

    public async Task<IReadOnlyList<EndpointNode>> ListEndpointsAsync(
        Guid workspaceId, string? serviceName, string? method, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);

        // Compose cypher dynamically depending on which filters are set. Method
        // is passed via params (case-insensitive match); service name flows via
        // params as well.
        string cypher;
        object parameters;

        var methodFilter = string.IsNullOrWhiteSpace(method)
            ? string.Empty
            : " AND toLower(e.method) = toLower($method)";

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            cypher = $$"""
                MATCH (s:Service {workspace_id: $ws, name: $name})-[:EXPOSES]->(e:Endpoint {workspace_id: $ws})
                WHERE true{{methodFilter}}
                RETURN e
                ORDER BY e.path, e.method
            """;
            parameters = method is null
                ? new { ws = workspaceId, name = serviceName }
                : (object)new { ws = workspaceId, name = serviceName, method };
        }
        else
        {
            cypher = $$"""
                MATCH (e:Endpoint {workspace_id: $ws})
                WHERE true{{methodFilter}}
                RETURN e
                ORDER BY e.path, e.method
            """;
            parameters = method is null
                ? new { ws = workspaceId }
                : (object)new { ws = workspaceId, method };
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher, parameters, "e", ct).ConfigureAwait(false);
        return rows.Select(MapEndpoint).ToList();
    }

    public async Task<IReadOnlyList<EndpointCaller>> FindEndpointCallersByIdAsync(
        Guid workspaceId, Guid endpointId, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        if (endpointId == Guid.Empty)
        {
            throw new ArgumentException("endpointId must be non-empty.", nameof(endpointId));
        }

        const string cypher = """
            MATCH (caller {workspace_id: $ws})-[:CALLS]->(e:Endpoint {workspace_id: $ws, id: $eid})
            RETURN caller
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher,
            new { ws = workspaceId, eid = endpointId }, "caller", ct).ConfigureAwait(false);

        return rows.Select(MapCaller).Where(x => x is not null).Cast<EndpointCaller>().ToList();
    }

    public async Task<IReadOnlyList<EndpointCaller>> FindEndpointCallersByRouteAsync(
        Guid workspaceId, string method, string path, CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        const string cypher = """
            MATCH (caller {workspace_id: $ws})-[:CALLS]->(e:Endpoint {workspace_id: $ws})
            WHERE toLower(e.method) = toLower($method) AND e.path = $path
            RETURN caller
        """;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await QueryColumnAsync(conn, cypher,
            new { ws = workspaceId, method, path }, "caller", ct).ConfigureAwait(false);

        return rows.Select(MapCaller).Where(x => x is not null).Cast<EndpointCaller>().ToList();
    }

    private static EndpointCaller? MapCaller(string raw)
    {
        using var doc = AgtypeParser.Parse(raw);
        if (doc is null) return null;
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;
        var label = doc.RootElement.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() ?? string.Empty
            : string.Empty;
        var id = AgtypeParser.GetGuid(props, "id") ?? Guid.Empty;
        if (id == Guid.Empty) return null;
        var name = AgtypeParser.GetString(props, "name");
        return new EndpointCaller(id, label, name);
    }

    // PERF: per-label substring scan. AGE cannot index against
    // CONTAINS predicates, so each label is filtered in Cypher and ranked
    // client-side. Result set is small (limit-bound).
    public async Task<IReadOnlyList<NodeSearchHit>> SearchNodesByTextAsync(
        Guid workspaceId,
        IEnumerable<string> tokens,
        int limit,
        CancellationToken ct = default)
    {
        EnsureScoped(workspaceId);
        var tokenList = tokens?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList() ?? new List<string>();
        if (tokenList.Count == 0)
        {
            return Array.Empty<NodeSearchHit>();
        }

        if (limit <= 0) limit = 25;
        if (limit > 500) limit = 500;

        var perLabelLimit = Math.Max(5, limit);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var aggregated = new List<NodeSearchHit>();

        foreach (var label in GraphLabels.Vertex)
        {
            // Build an OR-chain of CONTAINS predicates over name and description
            // using positional parameter names ($t0, $t1, …).
            var predicates = new List<string>(tokenList.Count * 2);
            var paramDict = new Dictionary<string, object>(StringComparer.Ordinal) { ["ws"] = workspaceId };
            for (var i = 0; i < tokenList.Count; i++)
            {
                var key = $"t{i}";
                paramDict[key] = tokenList[i];
                predicates.Add($"toLower(coalesce(n.name, '')) CONTAINS ${key}");
                predicates.Add($"toLower(coalesce(n.description, '')) CONTAINS ${key}");
            }

            var cypher = $$"""
                MATCH (n:{{label}} {workspace_id: $ws})
                WHERE {{string.Join(" OR ", predicates)}}
                RETURN n
                LIMIT {{perLabelLimit}}
            """;

            var rows = await QueryColumnAsync(conn, cypher, paramDict, "n", ct).ConfigureAwait(false);
            foreach (var raw in rows)
            {
                using var doc = AgtypeParser.Parse(raw);
                if (doc is null) continue;
                var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;
                var id = AgtypeParser.GetGuid(props, "id") ?? Guid.Empty;
                if (id == Guid.Empty) continue;
                var name = AgtypeParser.GetString(props, "name");
                var description = AgtypeParser.GetString(props, "description");
                var propsDict = AgtypeParser.ExtractProperties(raw);
                aggregated.Add(new NodeSearchHit(id, label, name, description, propsDict));
                if (aggregated.Count >= limit) break;
            }

            if (aggregated.Count >= limit) break;
        }

        return aggregated.Take(limit).ToList();
    }

    // ── Cypher execution helpers ──────────────────────────────────────────

    /// <summary>
    /// Execute a Cypher query that returns a single named agtype column on
    /// each row. Returns the raw agtype strings.
    /// </summary>
    private static async Task<IReadOnlyList<string>> QueryColumnAsync(
        DbConnection conn, string cypher, object parameters, string columnAlias, CancellationToken ct)
    {
        var sql = BuildCypherSql(cypher, new[] { columnAlias });
        var jsonParams = BuildJsonParams(parameters);

        var rows = await conn.QueryAsync<string?>(
            new CommandDefinition(sql, new { @params = jsonParams }, cancellationToken: ct))
            .ConfigureAwait(false);

        return rows.Where(r => r is not null).Cast<string>().ToList();
    }

    /// <summary>
    /// Execute a Cypher returning a single named column and at most one row.
    /// </summary>
    private static async Task<string?> QuerySingleColumnAsync(
        DbConnection conn, string cypher, object parameters, string columnAlias, CancellationToken ct)
    {
        var sql = BuildCypherSql(cypher, new[] { columnAlias });
        var jsonParams = BuildJsonParams(parameters);

        return await conn.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(sql, new { @params = jsonParams }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Execute a Cypher returning multiple named agtype columns. Returns one
    /// raw-string array per row, in <paramref name="columnAliases"/> order.
    /// </summary>
    private static async Task<IReadOnlyList<string?[]>> QueryMultiColumnAsync(
        DbConnection conn, string cypher, object parameters,
        IReadOnlyList<string> columnAliases, CancellationToken ct)
    {
        var sql = BuildCypherSql(cypher, columnAliases);
        var jsonParams = BuildJsonParams(parameters);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "params";
        p.Value = jsonParams;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<string?[]>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var arr = new string?[columnAliases.Count];
            for (var i = 0; i < columnAliases.Count; i++)
            {
                arr[i] = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i)?.ToString();
            }
            rows.Add(arr);
        }
        return rows;
    }

    /// <summary>
    /// Build the outer Postgres SQL that wraps a Cypher block. AGE syntax:
    /// <code>SELECT * FROM cypher('archmind_graph', $$ ... $$, @params) AS (c1 agtype, c2 agtype, ...);</code>
    /// </summary>
    private static string BuildCypherSql(string cypher, IReadOnlyList<string> columnAliases)
    {
        var columns = string.Join(", ", columnAliases.Select(a => $"{a} agtype"));
        return $"SELECT * FROM cypher('{GraphName}', $${cypher}$$, @params) AS ({columns});";
    }

    /// <summary>
    /// Convert an anonymous parameter object into the JSON string AGE expects
    /// as the <c>cypher()</c> third argument. Guids serialise as strings so
    /// they line up with how the writer stores <c>id</c> properties.
    /// </summary>
    private static string BuildJsonParams(object parameters)
    {
        if (parameters is null)
        {
            return "{}";
        }

        // System.Text.Json handles primitive props on anonymous types fine,
        // but it renders Guids as quoted strings by default — exactly what we
        // want.
        return JsonSerializer.Serialize(parameters, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    // ── Mapping helpers ───────────────────────────────────────────────────

    private static ServiceNode MapService(string raw)
    {
        using var doc = AgtypeParser.Parse(raw)
            ?? throw new InvalidOperationException("Expected vertex, got null agtype.");
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;

        return new ServiceNode(
            Id: AgtypeParser.GetGuid(props, "id") ?? Guid.Empty,
            Name: AgtypeParser.GetString(props, "name") ?? string.Empty,
            Purpose: AgtypeParser.GetString(props, "purpose"),
            RepoId: AgtypeParser.GetGuid(props, "repo_id"),
            RootPath: AgtypeParser.GetString(props, "root_path"),
            TechStack: AgtypeParser.GetStringArray(props, "tech_stack"));
    }

    private static EndpointNode MapEndpoint(string raw)
    {
        using var doc = AgtypeParser.Parse(raw)
            ?? throw new InvalidOperationException("Expected vertex, got null agtype.");
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;

        return new EndpointNode(
            Id: AgtypeParser.GetGuid(props, "id") ?? Guid.Empty,
            Method: AgtypeParser.GetString(props, "method") ?? string.Empty,
            Path: AgtypeParser.GetString(props, "path") ?? string.Empty,
            HandlerFile: AgtypeParser.GetString(props, "handler_file"));
    }

    private static DependencyNode MapDependency(string raw)
    {
        using var doc = AgtypeParser.Parse(raw)
            ?? throw new InvalidOperationException("Expected vertex, got null agtype.");
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;

        return new DependencyNode(
            Id: AgtypeParser.GetGuid(props, "id") ?? Guid.Empty,
            Name: AgtypeParser.GetString(props, "name") ?? string.Empty,
            Type: AgtypeParser.GetString(props, "type") ?? string.Empty,
            Version: AgtypeParser.GetString(props, "version"));
    }

    private static ConventionNode MapConvention(string raw)
    {
        using var doc = AgtypeParser.Parse(raw)
            ?? throw new InvalidOperationException("Expected vertex, got null agtype.");
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;

        return new ConventionNode(
            Id: AgtypeParser.GetGuid(props, "id") ?? Guid.Empty,
            Category: AgtypeParser.GetString(props, "category") ?? string.Empty,
            Name: AgtypeParser.GetString(props, "name") ?? string.Empty,
            Description: AgtypeParser.GetString(props, "description") ?? string.Empty);
    }

    private static EventRef MapEventRef(string raw)
    {
        using var doc = AgtypeParser.Parse(raw)
            ?? throw new InvalidOperationException("Expected vertex, got null agtype.");
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;

        return new EventRef(
            Id: AgtypeParser.GetGuid(props, "id") ?? Guid.Empty,
            Name: AgtypeParser.GetString(props, "name") ?? string.Empty);
    }

    /// <summary>
    /// Build an <see cref="EdgeRef"/> from a (rel, other, r) row produced by
    /// the GetNode incoming/outgoing queries.
    /// </summary>
    private static EdgeRef? MapEdgeRow(string?[] row)
    {
        if (row.Length < 3) return null;
        var relRaw = row[0];
        var otherRaw = row[1];
        var edgeRaw = row[2];

        var label = UnquoteAgtypeString(relRaw) ?? string.Empty;
        if (string.IsNullOrEmpty(label) || otherRaw is null) return null;

        using var otherDoc = AgtypeParser.Parse(otherRaw);
        if (otherDoc is null) return null;

        var otherLabel = otherDoc.RootElement.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() ?? string.Empty
            : string.Empty;

        var otherProps = otherDoc.RootElement.TryGetProperty("properties", out var op) ? op : default;
        var otherId = AgtypeParser.GetGuid(otherProps, "id") ?? Guid.Empty;
        var otherName = AgtypeParser.GetString(otherProps, "name");

        var edgeProps = AgtypeParser.ExtractProperties(edgeRaw);

        return new EdgeRef(label, otherId, otherLabel, otherName, edgeProps);
    }

    // ── Scalar helpers ────────────────────────────────────────────────────

    private static Guid? ParseGuidScalar(string? raw)
    {
        if (raw is null) return null;
        using var doc = AgtypeParser.Parse(raw);
        if (doc is null) return null;
        return AgtypeParser.TryGetGuid(doc.RootElement, out var g) ? g : null;
    }

    private static int? ParseIntScalar(string? raw)
    {
        if (raw is null) return null;
        using var doc = AgtypeParser.Parse(raw);
        if (doc is null) return null;
        if (doc.RootElement.ValueKind == JsonValueKind.Number &&
            doc.RootElement.TryGetInt32(out var i))
        {
            return i;
        }
        // count() can come back as a wider int — narrow safely.
        if (doc.RootElement.ValueKind == JsonValueKind.Number &&
            doc.RootElement.TryGetInt64(out var l))
        {
            return l > int.MaxValue ? int.MaxValue : (int)l;
        }
        return null;
    }

    /// <summary>
    /// Strip surrounding quotes from a scalar agtype string literal. AGE
    /// returns string scalars as JSON-quoted, e.g. <c>"PUBLISHES"</c>.
    /// </summary>
    private static string? UnquoteAgtypeString(string? raw)
    {
        if (raw is null) return null;
        using var doc = AgtypeParser.Parse(raw);
        if (doc is null) return null;
        return doc.RootElement.ValueKind == JsonValueKind.String
            ? doc.RootElement.GetString()
            : doc.RootElement.GetRawText();
    }

    private static Guid? TryReadGuidProperty(IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    // ── Invariants ────────────────────────────────────────────────────────

    /// <summary>
    /// Refuse to build any query without a workspace anchor. Public for
    /// unit testability per BE-022 acceptance criterion 2.
    /// </summary>
    internal static void EnsureScoped(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException(
                "workspaceId is required — graph reads MUST be workspace-scoped.",
                nameof(workspaceId));
        }
    }

    internal static void EnsureKnownVertexLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!GraphLabels.IsVertex(label))
        {
            throw new ArgumentException(
                $"Unknown vertex label '{label}'. Allowed: {string.Join(", ", GraphLabels.Vertex)}.",
                nameof(label));
        }
    }
}
