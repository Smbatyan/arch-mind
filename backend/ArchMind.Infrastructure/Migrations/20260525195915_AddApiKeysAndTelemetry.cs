using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeysAndTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_call_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    output_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cache_read_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cache_write_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", nullable: false, defaultValue: 0m),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    cache_hit = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_call_logs", x => x.id);
                    table.ForeignKey(
                        name: "FK_llm_call_logs_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_telemetry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    method = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    request_size_bytes = table.Column<int>(type: "integer", nullable: true),
                    response_size_bytes = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_telemetry", x => x.id);
                    table.ForeignKey(
                        name: "FK_mcp_telemetry_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_api_keys_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_call_logs_workspace_created_desc",
                table: "llm_call_logs",
                columns: new[] { "workspace_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_llm_call_logs_workspace_purpose",
                table: "llm_call_logs",
                columns: new[] { "workspace_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_telemetry_workspace_created_desc",
                table: "mcp_telemetry",
                columns: new[] { "workspace_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_telemetry_workspace_method",
                table: "mcp_telemetry",
                columns: new[] { "workspace_id", "method" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_api_keys_key_hash",
                table: "workspace_api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspace_api_keys_workspace_revoked",
                table: "workspace_api_keys",
                columns: new[] { "workspace_id", "revoked_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_call_logs");

            migrationBuilder.DropTable(
                name: "mcp_telemetry");

            migrationBuilder.DropTable(
                name: "workspace_api_keys");
        }
    }
}
