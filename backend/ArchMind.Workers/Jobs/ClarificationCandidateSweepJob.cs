using System.Security.Cryptography;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Core.Models.Graph;
using ArchMind.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Workers.Jobs;

/// <summary>
/// BE-042 (Sprint 5): per-workspace recurring sweep that keeps the
/// <c>clarifications</c> table healthy.
/// <list type="number">
///   <item>Auto-dismiss Open rows older than 90 days (single SQL update).</item>
///   <item>Orphan cleanup: dismiss Opens whose <c>RelatedNodeNames</c> no longer
///         resolve to any graph node.</item>
///   <item>Duplicate-Service detection: cluster Service node names by their
///         normalized form; submit one clarification per ambiguous cluster.</item>
///   <item>Dangling endpoints: for the first N endpoints with no callers,
///         submit a clarification asking whether they can be removed.</item>
/// </list>
/// All new candidates flow through <see cref="IClarificationIntake.SubmitAsync"/>
/// so priority + dedupe happen the same way they would for synchronous
/// extraction-time clarifications. The job is capped at 50 submissions per run
/// to bound LLM cost.
/// </summary>
public sealed class ClarificationCandidateSweepJob
{
    private const int OrphanRowCap = 200;
    private const int ServiceClusterMinSize = 2;
    private const int EndpointInspectionCap = 50;
    private const int DanglingEndpointEmissionCap = 20;
    private const int MaxSubmissionsPerRun = 50;

    private readonly ArchMindDbContext _db;
    private readonly IClarificationIntake _intake;
    private readonly IClarificationQuestionGenerator _generator;
    private readonly IGraphReader _graphReader;
    private readonly IFileExtractionRepository _fileExtractions;
    private readonly ILogger<ClarificationCandidateSweepJob> _logger;

    public ClarificationCandidateSweepJob(
        ArchMindDbContext db,
        IClarificationIntake intake,
        IClarificationQuestionGenerator generator,
        IGraphReader graphReader,
        IFileExtractionRepository fileExtractions,
        ILogger<ClarificationCandidateSweepJob> logger)
    {
        _db = db;
        _intake = intake;
        _generator = generator;
        _graphReader = graphReader;
        _fileExtractions = fileExtractions;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(Guid workspaceId, CancellationToken ct)
    {
        if (workspaceId == Guid.Empty)
        {
            _logger.LogWarning("ClarificationCandidateSweepJob skipped: empty workspace id");
            return;
        }

        _logger.LogInformation(
            "ClarificationCandidateSweepJob starting workspace={WorkspaceId}",
            workspaceId);

        var submittedQuestions = 0;

        // --- 1. Stale expiry (single SQL statement) ----------------------------
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
            var now = DateTimeOffset.UtcNow;
            var openValue = nameof(ClarificationStatus.Open);
            var dismissedValue = nameof(ClarificationStatus.Dismissed);
            const string staleAnswer = "[auto-dismissed] stale > 90d";

            var staleRowsUpdated = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE clarifications
                SET status = {dismissedValue},
                    answer = {staleAnswer},
                    updated_at = {now}
                WHERE workspace_id = {workspaceId}
                  AND status = {openValue}
                  AND created_at < {cutoff}", ct);

            if (staleRowsUpdated > 0)
            {
                _logger.LogInformation(
                    "ClarificationCandidateSweepJob stale-expiry rows={Rows} workspace={WorkspaceId}",
                    staleRowsUpdated, workspaceId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ClarificationCandidateSweepJob stale-expiry failed workspace={WorkspaceId}",
                workspaceId);
        }

        // --- 2. Orphan cleanup ------------------------------------------------
        try
        {
            await OrphanCleanupAsync(workspaceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ClarificationCandidateSweepJob orphan cleanup failed workspace={WorkspaceId}",
                workspaceId);
        }

        // --- 3. Duplicate Service detection -----------------------------------
        if (submittedQuestions <= MaxSubmissionsPerRun)
        {
            try
            {
                submittedQuestions = await DetectDuplicateServicesAsync(workspaceId, submittedQuestions, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "ClarificationCandidateSweepJob duplicate-service detection failed workspace={WorkspaceId}",
                    workspaceId);
            }
        }

        if (submittedQuestions > MaxSubmissionsPerRun)
        {
            _logger.LogWarning(
                "ClarificationCandidateSweepJob hit cost guardrail workspace={WorkspaceId} submitted={Submitted}",
                workspaceId, submittedQuestions);
            return;
        }

        // --- 4. Dangling endpoints --------------------------------------------
        try
        {
            submittedQuestions = await DetectDanglingEndpointsAsync(workspaceId, submittedQuestions, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ClarificationCandidateSweepJob dangling-endpoint detection failed workspace={WorkspaceId}",
                workspaceId);
        }

        _logger.LogInformation(
            "ClarificationCandidateSweepJob complete workspace={WorkspaceId} submitted={Submitted}",
            workspaceId, submittedQuestions);
    }

    // -------------------------------------------------------------------------
    // Step 2: orphan cleanup
    // -------------------------------------------------------------------------

    private async Task OrphanCleanupAsync(Guid workspaceId, CancellationToken ct)
    {
        // Load up to OrphanRowCap candidate rows. Filter "non-empty RelatedNodeNames"
        // in memory because EF doesn't lower array_length(...) cleanly across
        // providers, and the cap keeps the result set small.
        var candidates = await _db.Clarifications
            .Where(c => c.WorkspaceId == workspaceId && c.Status == ClarificationStatus.Open)
            .OrderBy(c => c.CreatedAt)
            .Take(OrphanRowCap)
            .ToListAsync(ct);

        var dismissed = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (c.RelatedNodeNames is null || c.RelatedNodeNames.Length == 0) continue;

            var anyAlive = false;
            foreach (var name in c.RelatedNodeNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                try
                {
                    var hits = await _graphReader.SearchNodesByTextAsync(
                        workspaceId, new[] { name }, 1, ct);
                    if (hits is { Count: > 0 })
                    {
                        anyAlive = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // Treat lookup failure as "still alive" to stay conservative —
                    // we don't want a transient graph error to mass-dismiss rows.
                    _logger.LogDebug(
                        ex,
                        "Orphan check lookup failed; treating as alive workspace={WorkspaceId} name={Name}",
                        workspaceId, name);
                    anyAlive = true;
                    break;
                }
            }

            if (!anyAlive)
            {
                c.Status = ClarificationStatus.Dismissed;
                c.Answer = "[auto-dismissed] referenced nodes no longer exist";
                c.UpdatedAt = DateTimeOffset.UtcNow;
                dismissed++;
            }
        }

        if (dismissed > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "ClarificationCandidateSweepJob orphan dismissals workspace={WorkspaceId} dismissed={Dismissed}",
                workspaceId, dismissed);
        }
    }

    // -------------------------------------------------------------------------
    // Step 3: duplicate Service detection
    // -------------------------------------------------------------------------

    private async Task<int> DetectDuplicateServicesAsync(
        Guid workspaceId,
        int submittedQuestions,
        CancellationToken ct)
    {
        var overview = await _graphReader.GetOverviewAsync(workspaceId, ct);
        if (!overview.NodeCounts.TryGetValue("Service", out var serviceCount) || serviceCount < 2)
        {
            return submittedQuestions;
        }

        var services = await _graphReader.ListServicesAsync(workspaceId, ct);
        if (services.Count < 2) return submittedQuestions;

        // Group by normalized name (lowercased, hyphens / underscores / spaces stripped).
        var clusters = services
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => NormalizeServiceName(s.Name))
            .Where(g => g.Key.Length > 0
                     && g.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() >= ServiceClusterMinSize)
            .ToList();

        foreach (var cluster in clusters)
        {
            if (submittedQuestions > MaxSubmissionsPerRun)
            {
                _logger.LogWarning(
                    "Cost guardrail hit mid duplicate-service detection workspace={WorkspaceId} submitted={Submitted}",
                    workspaceId, submittedQuestions);
                return submittedQuestions;
            }

            var normalized = cluster.Key;
            var originalNames = cluster
                .Select(s => s.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var sourceFilesByService = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var svc in cluster)
            {
                // Pull SourceFiles from the per-file extraction repository when
                // possible. ServiceNode itself doesn't carry SourceFiles, so we
                // look back at the recorded extractions for any path the graph
                // wrote into the Service's properties. Best effort: empty list
                // if we can't resolve.
                sourceFilesByService[svc.Name] = await TryGetServiceSourceFilesAsync(
                    workspaceId, svc, ct);
            }

            var unionFiles = sourceFilesByService.Values
                .SelectMany(v => v)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

            var evidenceMarkdown = BuildDuplicateServiceEvidence(normalized, originalNames, sourceFilesByService);

            var evidence = new ClarificationEvidence(
                Subject: "duplicate_service:" + normalized,
                EvidenceMarkdown: evidenceMarkdown,
                RelatedFilePaths: unionFiles,
                RelatedNodeNames: originalNames);

            submittedQuestions = await EmitFromEvidenceAsync(
                workspaceId,
                repoId: null,
                evidence,
                fallbackQuestion: $"Multiple services share name {normalized}. Are they the same service?",
                fallbackChoices: originalNames.Select(n => (string?)n).ToArray(),
                fallbackSeverity: "medium",
                fallbackTopicSuffix: normalized,
                submittedQuestions,
                ct);

            if (submittedQuestions > MaxSubmissionsPerRun)
            {
                return submittedQuestions;
            }
        }

        return submittedQuestions;
    }

    private async Task<IReadOnlyList<string>> TryGetServiceSourceFilesAsync(
        Guid workspaceId,
        ServiceNode service,
        CancellationToken ct)
    {
        try
        {
            // Single-token search by service name; pull any node properties that
            // surface source_files for the matching Service vertex.
            var hits = await _graphReader.SearchNodesByTextAsync(
                workspaceId, new[] { service.Name }, 5, ct);

            foreach (var hit in hits)
            {
                if (!string.Equals(hit.Label, "Service", StringComparison.Ordinal)) continue;
                if (!string.Equals(hit.Name, service.Name, StringComparison.Ordinal)) continue;
                if (hit.Properties is null) continue;

                if (hit.Properties.TryGetValue("source_files", out var raw)
                    && raw is IEnumerable<object?> seq)
                {
                    return seq
                        .Select(o => o?.ToString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Service source-files lookup failed workspace={WorkspaceId} service={Service}",
                workspaceId, service.Name);
        }
        return Array.Empty<string>();
    }

    private static string BuildDuplicateServiceEvidence(
        string normalized,
        IReadOnlyList<string> originalNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> sourceFilesByService)
    {
        var sb = new StringBuilder();
        sb.Append("**Normalized name:** `").Append(normalized).Append("`\n\n");
        sb.Append("**Candidate originals:**\n\n");
        foreach (var name in originalNames)
        {
            sb.Append("- `").Append(name).Append('`');
            if (sourceFilesByService.TryGetValue(name, out var files) && files.Count > 0)
            {
                sb.Append(" — ");
                sb.Append(string.Join(", ", files.Take(3).Select(f => "`" + f + "`")));
                if (files.Count > 3) sb.Append(", …");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Step 4: dangling endpoints
    // -------------------------------------------------------------------------

    private async Task<int> DetectDanglingEndpointsAsync(
        Guid workspaceId,
        int submittedQuestions,
        CancellationToken ct)
    {
        var endpoints = await _graphReader.ListEndpointsAsync(workspaceId, null, null, ct);
        if (endpoints.Count == 0) return submittedQuestions;

        var emitted = 0;
        var inspected = 0;

        foreach (var ep in endpoints)
        {
            if (inspected >= EndpointInspectionCap) break;
            inspected++;

            if (submittedQuestions > MaxSubmissionsPerRun)
            {
                _logger.LogWarning(
                    "Cost guardrail hit mid dangling-endpoint detection workspace={WorkspaceId} submitted={Submitted}",
                    workspaceId, submittedQuestions);
                return submittedQuestions;
            }

            IReadOnlyList<EndpointCaller> callers;
            try
            {
                callers = await _graphReader.FindEndpointCallersByIdAsync(workspaceId, ep.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Dangling endpoint caller lookup failed; skipping workspace={WorkspaceId} endpoint={EndpointId}",
                    workspaceId, ep.Id);
                continue;
            }

            if (callers.Count > 0) continue;

            // Owning service: the EndpointNode DTO doesn't carry one, but the
            // 1-hop NodeDetail surfaces incoming EXPOSES from a Service. Treat
            // "no service exposes this endpoint" as no owner. Best effort.
            var hasOwner = await EndpointHasOwningServiceAsync(workspaceId, ep.Id, ct);
            if (hasOwner) continue;

            // Build evidence + emit.
            var relatedFiles = string.IsNullOrWhiteSpace(ep.HandlerFile)
                ? Array.Empty<string>()
                : new[] { ep.HandlerFile! };

            var subject = $"dangling_endpoint:{ep.Method} {ep.Path}";
            var evidenceMarkdown = BuildDanglingEndpointEvidence(ep);

            var evidence = new ClarificationEvidence(
                Subject: subject,
                EvidenceMarkdown: evidenceMarkdown,
                RelatedFilePaths: relatedFiles,
                RelatedNodeNames: new[] { $"{ep.Method} {ep.Path}" });

            submittedQuestions = await EmitFromEvidenceAsync(
                workspaceId,
                repoId: null,
                evidence,
                fallbackQuestion: $"Endpoint {ep.Method} {ep.Path} has no callers and no owner. Should it be removed?",
                fallbackChoices: new string?[] { "Remove it", "Keep it", "Add a caller" },
                fallbackSeverity: "low",
                fallbackTopicSuffix: $"{ep.Method}_{ep.Path}",
                submittedQuestions,
                ct);

            emitted++;
            if (emitted >= DanglingEndpointEmissionCap) break;
        }

        return submittedQuestions;
    }

    private async Task<bool> EndpointHasOwningServiceAsync(Guid workspaceId, Guid endpointId, CancellationToken ct)
    {
        try
        {
            var detail = await _graphReader.GetNodeAsync(workspaceId, "Endpoint", endpointId, ct);
            if (detail is null) return false;

            // Service ──EXPOSES──▶ Endpoint, so an owning service shows up as
            // an incoming edge with the EXPOSES label whose other end is a
            // Service. We accept any incoming edge from a Service as "owned".
            foreach (var edge in detail.IncomingEdges)
            {
                if (string.Equals(edge.OtherNodeLabel, "Service", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Endpoint owner lookup failed; treating as owned (conservative) workspace={WorkspaceId} endpoint={EndpointId}",
                workspaceId, endpointId);
            return true;
        }
    }

    private static string BuildDanglingEndpointEvidence(EndpointNode ep)
    {
        var sb = new StringBuilder();
        sb.Append("**Method:** `").Append(ep.Method).Append("`  \n");
        sb.Append("**Path:** `").Append(ep.Path).Append("`  \n");
        if (!string.IsNullOrWhiteSpace(ep.HandlerFile))
        {
            sb.Append("**Handler file:** `").Append(ep.HandlerFile).Append("`  \n");
        }
        sb.Append('\n');
        sb.Append("No incoming CALLS edges were found in the graph, and no Service node EXPOSES this endpoint.");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Generator + intake helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asks the question generator for one or more rephrased clarifying
    /// questions, then runs each through intake. On empty generator output or
    /// any error, falls back to a single heuristic question so the candidate
    /// isn't lost. Returns the (possibly incremented) submission counter.
    /// </summary>
    private async Task<int> EmitFromEvidenceAsync(
        Guid workspaceId,
        Guid? repoId,
        ClarificationEvidence evidence,
        string fallbackQuestion,
        string?[] fallbackChoices,
        string fallbackSeverity,
        string fallbackTopicSuffix,
        int submittedQuestions,
        CancellationToken ct)
    {
        IReadOnlyList<GeneratedQuestion> generated = Array.Empty<GeneratedQuestion>();
        try
        {
            generated = await _generator.GenerateAsync(workspaceId, repoId, evidence, ct)
                       ?? Array.Empty<GeneratedQuestion>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Clarification question generator threw; using heuristic fallback workspace={WorkspaceId} subject={Subject}",
                workspaceId, evidence.Subject);
        }

        if (generated.Count == 0)
        {
            var heuristic = BuildClarification(
                workspaceId,
                repoId,
                topic: Truncate(evidence.Subject, 200),
                question: Truncate(fallbackQuestion, 2000),
                context: evidence.EvidenceMarkdown,
                choices: fallbackChoices,
                relatedFiles: evidence.RelatedFilePaths.ToArray(),
                relatedNodes: evidence.RelatedNodeNames.ToArray());

            submittedQuestions = await TrySubmitAsync(heuristic, fallbackSeverity, submittedQuestions, ct);
            return submittedQuestions;
        }

        foreach (var q in generated)
        {
            if (submittedQuestions > MaxSubmissionsPerRun) return submittedQuestions;
            if (q is null) continue;

            var topic = Truncate(string.IsNullOrWhiteSpace(q.Topic) ? evidence.Subject : q.Topic, 200);
            var question = Truncate(string.IsNullOrWhiteSpace(q.Question) ? fallbackQuestion : q.Question, 2000);
            var choices = (q.Choices ?? Array.Empty<string>())
                .Select(c => (string?)c)
                .ToArray();
            var relatedFiles = (q.RelatedFilePaths ?? Array.Empty<string>()).ToArray();
            var relatedNodes = (q.RelatedNodeNames ?? Array.Empty<string>()).ToArray();
            // Generator may omit related lists; fall back to evidence's.
            if (relatedFiles.Length == 0) relatedFiles = evidence.RelatedFilePaths.ToArray();
            if (relatedNodes.Length == 0) relatedNodes = evidence.RelatedNodeNames.ToArray();

            var severity = string.IsNullOrWhiteSpace(q.Severity) ? fallbackSeverity : q.Severity;

            var clarification = BuildClarification(
                workspaceId,
                repoId,
                topic,
                question,
                context: evidence.EvidenceMarkdown,
                choices,
                relatedFiles,
                relatedNodes);

            submittedQuestions = await TrySubmitAsync(clarification, severity, submittedQuestions, ct);
        }

        return submittedQuestions;
    }

    private async Task<int> TrySubmitAsync(
        Clarification candidate,
        string severity,
        int submittedQuestions,
        CancellationToken ct)
    {
        if (submittedQuestions > MaxSubmissionsPerRun) return submittedQuestions;
        try
        {
            await _intake.SubmitAsync(candidate, severity, ct);
            return submittedQuestions + 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Clarification intake submission failed workspace={WorkspaceId} topic={Topic} fingerprint={Fingerprint}",
                candidate.WorkspaceId, candidate.Topic, candidate.Fingerprint);
            // Counts the attempt against the guardrail to avoid loops on persistent errors.
            return submittedQuestions + 1;
        }
    }

    private static Clarification BuildClarification(
        Guid workspaceId,
        Guid? repoId,
        string topic,
        string question,
        string? context,
        string?[] choices,
        string[] relatedFiles,
        string[] relatedNodes)
    {
        var sortedFiles = relatedFiles
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        return new Clarification
        {
            WorkspaceId = workspaceId,
            RepoId = repoId,
            Source = ClarificationSource.ManualLlmGen,
            Topic = topic,
            Question = question,
            Context = context,
            Choices = choices ?? Array.Empty<string?>(),
            Priority = 50,
            Status = ClarificationStatus.Open,
            RelatedFilePaths = sortedFiles,
            RelatedNodeNames = relatedNodes ?? Array.Empty<string>(),
            Fingerprint = ComputeFingerprint(workspaceId, topic, question, sortedFiles),
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Normalize a service name to detect near-duplicates: lower-case, strip
    /// hyphens / underscores / whitespace / common service suffix punctuation.
    /// "Orders-Service" and "orders_service" both collapse to "ordersservice".
    /// </summary>
    private static string NormalizeServiceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    private static string ComputeFingerprint(
        Guid workspaceId,
        string topic,
        string question,
        IReadOnlyList<string> sortedRelatedFiles)
    {
        var keyMaterial = sortedRelatedFiles.Count > 0
            ? string.Join("|", sortedRelatedFiles)
            : "<no-files>";
        var raw = $"{workspaceId:N}|{topic}|{question}|{keyMaterial}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value.Substring(0, max);
    }
}
