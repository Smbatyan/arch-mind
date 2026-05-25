using System.Data.Common;

namespace ArchMind.Core.Abstractions;

/// <summary>
/// Opens database connections for ad-hoc/Dapper consumers that sit outside the
/// EF Core <c>ArchMindDbContext</c>. The returned connection is already open and
/// (for Postgres/AGE consumers) has any per-connection bootstrap statements
/// executed (e.g. <c>LOAD 'age'</c>, <c>SET search_path</c>).
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Open and return a new <see cref="DbConnection"/>. Caller owns disposal.
    /// </summary>
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}
