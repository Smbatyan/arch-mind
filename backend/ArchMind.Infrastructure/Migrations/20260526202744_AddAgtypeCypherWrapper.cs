using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgtypeCypherWrapper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AGE 1.6+ requires cypher()'s third argument to be a Param node typed
            // as ag_catalog.agtype. Npgsql has no built-in serializer for agtype
            // and refuses to write String -> ag_catalog.agtype, throwing
            // "Writing values of 'System.String' is not supported...". This
            // wrapper accepts a JSONB parameter (which Npgsql writes natively),
            // casts it to agtype inside plpgsql, and runs cypher() via EXECUTE
            // USING — moving the agtype binding entirely server-side.
            //
            // Usage from C#:
            //   SELECT * FROM archmind_cypher_query('archmind_graph',
            //     $cy$ MATCH (n) WHERE n.id = $id RETURN n $cy$,
            //     @params::jsonb)
            //   AS r;
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION public.archmind_cypher_query(
                    graph_name text,
                    query_text text,
                    params_json jsonb
                )
                RETURNS SETOF ag_catalog.agtype
                LANGUAGE plpgsql
                AS $body$
                DECLARE
                    ag_params ag_catalog.agtype;
                    sql_text text;
                BEGIN
                    ag_params := params_json::text::ag_catalog.agtype;
                    -- Dollar-quote the cypher body inline so $name placeholders
                    -- inside it are treated as cypher params, not Postgres
                    -- positional parameters.
                    sql_text := 'SELECT * FROM ag_catalog.cypher('
                        || quote_literal(graph_name)
                        || ', $cy$' || query_text || '$cy$, $1) AS (r ag_catalog.agtype)';
                    RETURN QUERY EXECUTE sql_text USING ag_params;
                END
                $body$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.archmind_cypher_query(text, text, jsonb);");
        }
    }
}
