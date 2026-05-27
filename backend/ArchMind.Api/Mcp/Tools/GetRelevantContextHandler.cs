using System.Diagnostics;
using System.Text.Json;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Extraction;
using ArchMind.Core.Models.Graph;

namespace ArchMind.Api.Mcp.Tools;

/// <summary>
/// BE-031: implementation of the <c>get_relevant_context</c> MCP tool.
///
/// <para>
/// <b>Sibling integration (McpToolsHandler.cs):</b> register this tool in the
/// <c>tools/list</c> response and dispatch to it from <c>tools/call</c>.
/// </para>
///
/// <code>
/// // tools/list entry
/// {
///   "name": "get_relevant_context",
///   "description": "Returns matched user skills, relevant graph nodes, and
///                    file extraction summaries for an agent task, fitted to
///                    a token budget.",
///   "inputSchema": {
///     "type": "object",
///     "properties": {
///       "task":       { "type": "string", "description": "Agent task description" },
///       "repo":       { "type": "string", "description": "Optional repo identifier" },
///       "file_paths": { "type": "array", "items": { "type": "string" } },
///       "max_tokens": { "type": "integer", "default": 4000 }
///     },
///     "required": ["task"]
///   }
/// }
///
/// // tools/call dispatch
/// var handler = httpContext.RequestServices
///                          .GetRequiredService&lt;GetRelevantContextHandler&gt;();
/// var result  = await handler.ExecuteAsync(workspaceId, arguments, ct);
/// </code>
///
/// <para>
/// Workflow: skill match + graph token search + file extraction lookup are
/// dispatched in parallel via <c>Task.WhenAll</c>. The combined response is
/// trimmed to fit <c>max_tokens</c> using a (chars / 4) heuristic; on overflow,
/// graph hits are dropped first, then file extractions are trimmed, with
/// <c>truncated</c> flipping to <c>true</c>.
/// </para>
/// </summary>
public sealed class GetRelevantContextHandler
{
    /// <summary>Tool name exposed via the MCP <c>tools/list</c> response.</summary>
    public const string ToolName = "get_relevant_context";

    private const int DefaultMaxTokens = 4000;
    private const int MinMaxTokens = 256;
    private const int MaxMaxTokensCap = 32000;
    private const double CharsPerToken = 4.0;

    // Conservative English/code stop-word list used to filter task tokens
    // before graph search. Intentionally short — we want recall over precision
    // at this stage.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "this", "that", "these", "those",
        "with", "from", "into", "onto", "over", "under",
        "have", "having", "been", "being",
        "your", "yours", "their", "theirs",
        "what", "when", "where", "while", "which", "would", "could", "should",
        "about", "after", "before", "between",
        "also", "just", "very", "much", "many", "more", "most",
        "some", "such", "than", "then", "they", "them",
        "will", "shall",
        "please", "make", "made", "want", "need",
        "code", "file", "files",
    };

    private readonly ISkillMatcher _skills;
    private readonly IGraphReader _graph;
    private readonly IFileExtractionRepository _fileExtractions;
    private readonly ILogger<GetRelevantContextHandler> _logger;

    public GetRelevantContextHandler(
        ISkillMatcher skills,
        IGraphReader graph,
        IFileExtractionRepository fileExtractions,
        ILogger<GetRelevantContextHandler> logger)
    {
        _skills = skills;
        _graph = graph;
        _fileExtractions = fileExtractions;
        _logger = logger;
    }

    /// <summary>
    /// Execute the tool. <paramref name="arguments"/> is the JSON-RPC
    /// <c>tools/call</c> <c>arguments</c> object. The returned object is
    /// already shaped for serialization back to the MCP client.
    /// </summary>
    public async Task<object> ExecuteAsync(
        Guid workspaceId,
        JsonElement arguments,
        CancellationToken ct)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("workspaceId is required.", nameof(workspaceId));
        }

        var sw = Stopwatch.StartNew();

        var task = TryGetString(arguments, "task")?.Trim() ?? string.Empty;
        var repo = TryGetString(arguments, "repo");
        var filePaths = TryGetStringArray(arguments, "file_paths");
        var maxTokens = TryGetInt(arguments, "max_tokens") ?? DefaultMaxTokens;
        if (maxTokens < MinMaxTokens) maxTokens = MinMaxTokens;
        if (maxTokens > MaxMaxTokensCap) maxTokens = MaxMaxTokensCap;

        if (task.Length == 0)
        {
            return new
            {
                skills = Array.Empty<object>(),
                graph = Array.Empty<object>(),
                files = Array.Empty<object>(),
                token_estimate = 0,
                truncated = false,
                error = "task is required",
            };
        }

        var tokens = ExtractSignificantTokens(task);

        // Run all four lookups in parallel. Failures in any one bucket are
        // logged and degrade gracefully to an empty list — the tool should
        // never hard-fail a request just because graph search blew up.
        var skillsTask = SafeSkillsAsync(workspaceId, task, ct);
        var graphTask = SafeGraphAsync(workspaceId, tokens, ct);
        var filesTask = SafeFilesAsync(workspaceId, filePaths, ct);
        // Extraction search queries the incremental file-extraction layer (updated every 30 min
        // via DiffScan) for endpoints/events/storage matching the task tokens. This is always
        // fresher than the graphify structural graph, which only updates on full rescan.
        var extractionTask = SafeExtractionSearchAsync(workspaceId, tokens, ct);

        await Task.WhenAll(skillsTask, graphTask, filesTask, extractionTask).ConfigureAwait(false);

        var matchedSkills = skillsTask.Result;
        var graphHits = graphTask.Result;
        var fileRows = filesTask.Result;
        var extractionRows = extractionTask.Result;

        // ── Budget assembly ────────────────────────────────────────────────
        var skillItems = matchedSkills
            .Select(s => new SkillItem(s.Name, s.Title, s.Body))
            .ToList();

        var graphItems = graphHits
            .Select(h => new GraphItem(h.Label, h.Name ?? string.Empty, h.Description))
            .ToList();

        var fileItems = fileRows
            .Select(BuildFileItem)
            .ToList();

        var extractionItems = extractionRows
            .Select(BuildExtractionHitItem)
            .ToList();

        var truncated = false;

        // Compute total estimate. Skills are highest priority — never trimmed.
        // Drop order: graph first (stale, structural-only), then extraction hits,
        // then trim files (highest value — explicitly requested by caller).
        var skillChars = skillItems.Sum(CountChars);
        var graphChars = graphItems.Sum(CountChars);
        var fileChars = fileItems.Sum(CountChars);
        var extractionChars = extractionItems.Sum(CountChars);

        int Estimate(int chars) => (int)Math.Ceiling(chars / CharsPerToken);

        var estimate = Estimate(skillChars + graphChars + fileChars + extractionChars);

        if (estimate > maxTokens)
        {
            // Drop graph first (stale structural graph).
            graphItems = new List<GraphItem>();
            graphChars = 0;
            truncated = true;
            estimate = Estimate(skillChars + fileChars + extractionChars);
        }

        if (estimate > maxTokens && extractionItems.Count > 0)
        {
            // Drop extraction hits second.
            extractionItems = new List<ExtractionHitItem>();
            extractionChars = 0;
            truncated = true;
            estimate = Estimate(skillChars + fileChars);
        }

        if (estimate > maxTokens && fileItems.Count > 0)
        {
            // Trim file extractions from the tail until we fit (or run out).
            var trimmed = new List<FileItem>(fileItems);
            while (trimmed.Count > 0 && Estimate(skillChars + trimmed.Sum(CountChars)) > maxTokens)
            {
                trimmed.RemoveAt(trimmed.Count - 1);
            }
            fileItems = trimmed;
            truncated = true;
            estimate = Estimate(skillChars + fileItems.Sum(CountChars));
        }

        sw.Stop();
        _logger.LogInformation(
            "get_relevant_context completed. workspace={WorkspaceId} latencyMs={LatencyMs} skills={Skills} graph={Graph} extractions={Extractions} files={Files} tokens={Tokens} truncated={Truncated} repo={Repo}",
            workspaceId,
            sw.ElapsedMilliseconds,
            skillItems.Count,
            graphItems.Count,
            extractionItems.Count,
            fileItems.Count,
            estimate,
            truncated,
            repo ?? string.Empty);

        return new
        {
            skills = skillItems,
            graph = graphItems,
            extraction_hits = extractionItems,
            files = fileItems,
            token_estimate = estimate,
            truncated,
        };
    }

    // -----------------------------------------------------------------------
    // Lookups (each wrapped to swallow & log so the tool degrades gracefully)
    // -----------------------------------------------------------------------
    private async Task<IReadOnlyList<MatchedSkill>> SafeSkillsAsync(
        Guid workspaceId, string task, CancellationToken ct)
    {
        try
        {
            return await _skills.MatchAsync(workspaceId, task, maxSkills: 3, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill match failed for workspace {WorkspaceId}.", workspaceId);
            return Array.Empty<MatchedSkill>();
        }
    }

    private async Task<IReadOnlyList<NodeSearchHit>> SafeGraphAsync(
        Guid workspaceId, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        if (tokens.Count == 0) return Array.Empty<NodeSearchHit>();
        try
        {
            return await _graph.SearchNodesByTextAsync(workspaceId, tokens, limit: 20, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph search failed for workspace {WorkspaceId}.", workspaceId);
            return Array.Empty<NodeSearchHit>();
        }
    }

    private async Task<IReadOnlyList<FileExtractionRow>> SafeFilesAsync(
        Guid workspaceId, IReadOnlyList<string>? filePaths, CancellationToken ct)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            return Array.Empty<FileExtractionRow>();
        }
        try
        {
            return await _fileExtractions
                .GetLatestForFilesAsync(workspaceId, filePaths, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File extraction lookup failed for workspace {WorkspaceId}.", workspaceId);
            return Array.Empty<FileExtractionRow>();
        }
    }

    private async Task<IReadOnlyList<FileExtractionRow>> SafeExtractionSearchAsync(
        Guid workspaceId, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        if (tokens.Count == 0) return Array.Empty<FileExtractionRow>();
        try
        {
            return await _fileExtractions
                .SearchByTokensAsync(workspaceId, tokens, limit: 15, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extraction search failed for workspace {WorkspaceId}.", workspaceId);
            return Array.Empty<FileExtractionRow>();
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static IReadOnlyList<string> ExtractSignificantTokens(string task)
    {
        if (string.IsNullOrWhiteSpace(task)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in task.Split(
            new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length < 4) continue;
            if (Stopwords.Contains(token)) continue;
            if (!seen.Add(token)) continue;
            result.Add(token);
        }
        return result;
    }

    private static FileItem BuildFileItem(FileExtractionRow row)
    {
        var summary = BuildFileSummary(row.Record);
        var components = BuildComponentList(row.Record);
        return new FileItem(row.FilePath, summary, components);
    }

    private static string BuildFileSummary(FileExtractionRecord record)
    {
        var parts = new List<string>();
        if (record.Service is { IsPartOfService: true } svc)
        {
            var purpose = string.IsNullOrWhiteSpace(svc.ServicePurpose) ? null : svc.ServicePurpose;
            parts.Add(purpose is null
                ? $"Part of service '{svc.ServiceName}'."
                : $"Part of service '{svc.ServiceName}': {purpose}");
        }
        if (record.Endpoints is { Endpoints.Count: > 0 } ep)
        {
            parts.Add($"Defines {ep.Endpoints.Count} HTTP endpoint(s).");
        }
        if (record.EventsPublished is { Publishes.Count: > 0 } pub)
        {
            parts.Add($"Publishes {pub.Publishes.Count} event(s).");
        }
        if (record.EventsConsumed is { Consumes.Count: > 0 } con)
        {
            parts.Add($"Consumes {con.Consumes.Count} event(s).");
        }
        if (record.Storage is { Storages.Count: > 0 } st)
        {
            parts.Add($"Touches {st.Storages.Count} storage backend(s).");
        }
        if (record.Conventions is { Conventions.Count: > 0 } conv)
        {
            parts.Add($"Declares {conv.Conventions.Count} convention(s).");
        }
        return parts.Count == 0 ? "(no extractions on file)" : string.Join(" ", parts);
    }

    private static List<object> BuildComponentList(FileExtractionRecord record)
    {
        var list = new List<object>();
        if (record.Endpoints is { Endpoints.Count: > 0 } ep)
        {
            foreach (var e in ep.Endpoints)
            {
                list.Add(new { kind = "endpoint", method = e.Method, path = e.Path, handler = e.HandlerSymbol });
            }
        }
        if (record.EventsPublished is { Publishes.Count: > 0 } pub)
        {
            foreach (var e in pub.Publishes)
            {
                list.Add(new { kind = "event_publish", name = e.Name, version = e.Version, topic = e.Topic });
            }
        }
        if (record.EventsConsumed is { Consumes.Count: > 0 } con)
        {
            foreach (var e in con.Consumes)
            {
                list.Add(new { kind = "event_consume", name = e.Name, version = e.Version, topic = e.Topic });
            }
        }
        if (record.Storage is { Storages.Count: > 0 } st)
        {
            foreach (var s in st.Storages)
            {
                list.Add(new { kind = "storage", name = s.Name, type = s.Type, access = s.Access });
            }
        }
        if (record.Conventions is { Conventions.Count: > 0 } conv)
        {
            foreach (var c in conv.Conventions)
            {
                list.Add(new { kind = "convention", category = c.Category, name = c.Name, description = c.Description });
            }
        }
        return list;
    }

    private static ExtractionHitItem BuildExtractionHitItem(FileExtractionRow row)
    {
        var endpoints = row.Record.Endpoints?.Endpoints
            .Select(e => new EndpointHit(e.Method, e.Path, e.HandlerSymbol))
            .ToList() ?? new List<EndpointHit>();

        var eventsPublished = row.Record.EventsPublished?.Publishes
            .Select(e => new EventHit(e.Name, e.Topic))
            .ToList() ?? new List<EventHit>();

        var eventsConsumed = row.Record.EventsConsumed?.Consumes
            .Select(e => new EventHit(e.Name, e.Topic))
            .ToList() ?? new List<EventHit>();

        return new ExtractionHitItem(row.FilePath, endpoints, eventsPublished, eventsConsumed);
    }

    private static int CountChars(SkillItem s) =>
        (s.Name?.Length ?? 0) + (s.Title?.Length ?? 0) + (s.Body?.Length ?? 0);

    private static int CountChars(GraphItem g) =>
        (g.Label?.Length ?? 0) + (g.Name?.Length ?? 0) + (g.Description?.Length ?? 0);

    private static int CountChars(ExtractionHitItem e) =>
        (e.File?.Length ?? 0) +
        e.Endpoints.Sum(ep => (ep.Method?.Length ?? 0) + (ep.Path?.Length ?? 0) + (ep.Handler?.Length ?? 0)) +
        e.EventsPublished.Sum(ev => (ev.Name?.Length ?? 0) + (ev.Topic?.Length ?? 0)) +
        e.EventsConsumed.Sum(ev => (ev.Name?.Length ?? 0) + (ev.Topic?.Length ?? 0));

    private static int CountChars(FileItem f)
    {
        var chars = (f.Path?.Length ?? 0) + (f.Summary?.Length ?? 0);
        if (f.Components is not null)
        {
            // Each component is a small anonymous object; estimate ~80 chars each.
            chars += f.Components.Count * 80;
        }
        return chars;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static IReadOnlyList<string>? TryGetStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    // Internal response shapes — sealed records keep the JSON property order
    // stable across serializations.
    private sealed record SkillItem(string Name, string Title, string Body);
    private sealed record GraphItem(string Label, string Name, string? Description);
    private sealed record FileItem(string Path, string Summary, List<object> Components);
    private sealed record EndpointHit(string Method, string Path, string Handler);
    private sealed record EventHit(string Name, string? Topic);
    private sealed record ExtractionHitItem(
        string File,
        List<EndpointHit> Endpoints,
        List<EventHit> EventsPublished,
        List<EventHit> EventsConsumed);
}
