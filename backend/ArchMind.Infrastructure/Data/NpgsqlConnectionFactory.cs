using System.Data.Common;
using ArchMind.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ArchMind.Infrastructure.Data;

/// <summary>
/// Opens <see cref="NpgsqlConnection"/>s for Dapper-based callers (graph writer
/// / reader). Each connection is bootstrapped with the per-session AGE setup so
/// callers can immediately issue <c>SELECT * FROM cypher(...)</c> queries.
/// </summary>
/// <remarks>
/// AGE requires two things in every session:
/// <list type="number">
///   <item><c>LOAD 'age';</c> — loads the extension functions into the session.</item>
///   <item><c>SET search_path = ag_catalog, "$user", public;</c> — makes the
///   <c>cypher</c> function and <c>agtype</c> visible without qualification.</item>
/// </list>
/// We don't pool a custom data source here — Npgsql's built-in connection pool
/// handles physical connection reuse and we re-run the bootstrap on each
/// logical open (it's cheap and session-scoped, so it has to happen after each
/// physical reset).
/// </remarks>
internal sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured.");

        // Build a dedicated data source with unmapped types enabled. AGE's
        // `cypher()` function requires its third argument to be a parameter
        // typed as `ag_catalog.agtype`. Npgsql does not ship a built-in mapping
        // for agtype, so without EnableUnmappedTypes() it refuses to serialize
        // a .NET string into a parameter declared as that type, throwing
        // "Writing values of 'System.String' is not supported for parameters
        // having DataTypeName 'ag_catalog.agtype'". With the flag enabled
        // Npgsql falls back to the text encoder; Postgres then calls
        // `agtype_in()` to coerce the literal into a real agtype value.
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableUnmappedTypes();
        _dataSource = builder.Build();
    }

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // AGE per-session bootstrap. Must run after every physical connection
        // open. Cheap (no-op if already loaded in the backend process).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return conn;
    }
}
