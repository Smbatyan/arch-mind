using ArchMind.Core.Abstractions;
using Dapper;

namespace ArchMind.Infrastructure.Clarifications;

/// <summary>
/// BE-040 (Sprint 5): Dapper-backed implementation of
/// <see cref="IAnsweredClarificationLookup"/>. Filters answered clarifications
/// in the given workspace whose <c>related_file_paths</c> contains the file
/// path OR whose <c>related_node_names</c> overlaps the set of node names
/// already detected for the file under extraction.
/// </summary>
/// <remarks>
/// We use raw SQL via Dapper rather than EF Core's expression tree because
/// translating <c>text[] &amp;&amp; text[]</c> against a parameter array on both
/// sides of an OR gets fiddly with Npgsql's array type inference once you mix
/// it with EF's <c>FromSqlInterpolated</c>. The Dapper path lets us pass both
/// arrays as native Npgsql <c>text[]</c> parameters and keep the predicate
/// readable. Bounded to 25 most-recently-answered rows so a chatty file can't
/// blow up prompt length.
/// </remarks>
public sealed class AnsweredClarificationLookup : IAnsweredClarificationLookup
{
    private const int MaxResults = 25;

    private readonly IDbConnectionFactory _factory;

    public AnsweredClarificationLookup(IDbConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<IReadOnlyList<AnsweredClarification>> GetForFileAsync(
        Guid workspaceId,
        string filePath,
        IReadOnlyList<string> nodeNames,
        CancellationToken ct)
    {
        var path = filePath ?? string.Empty;
        var nodes = nodeNames is null
            ? Array.Empty<string>()
            : nodeNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();

        if (string.IsNullOrEmpty(path) && nodes.Length == 0)
        {
            return Array.Empty<AnsweredClarification>();
        }

        // We always pass a single-element array for the file-path side so the
        // SQL uses '&&' (array overlap) on both branches — same operator, same
        // index eligibility. Empty file path collapses to an empty array which
        // makes the left-hand side trivially false.
        var pathArray = string.IsNullOrEmpty(path)
            ? Array.Empty<string>()
            : new[] { path };

        const string sql = @"
            SELECT topic           AS Topic,
                   question        AS Question,
                   answer          AS Answer,
                   related_file_paths AS RelatedFilePaths
            FROM clarifications
            WHERE workspace_id = @WorkspaceId
              AND status = 'Answered'
              AND answer IS NOT NULL
              AND (
                    related_file_paths && @PathArray::text[]
                 OR related_node_names && @Nodes::text[]
              )
            ORDER BY answered_at DESC NULLS LAST
            LIMIT @Limit";

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var rows = await conn.QueryAsync<Row>(
            new CommandDefinition(
                sql,
                new
                {
                    WorkspaceId = workspaceId,
                    PathArray = pathArray,
                    Nodes = nodes,
                    Limit = MaxResults,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);

        var list = new List<AnsweredClarification>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Answer)) continue;
            list.Add(new AnsweredClarification(
                row.Topic ?? string.Empty,
                row.Question ?? string.Empty,
                row.Answer!,
                row.RelatedFilePaths ?? Array.Empty<string>()));
        }

        return list;
    }

    // Dapper row shape — kept private so we don't leak the column types.
    private sealed class Row
    {
        public string? Topic { get; set; }
        public string? Question { get; set; }
        public string? Answer { get; set; }
        public string[]? RelatedFilePaths { get; set; }
    }
}
