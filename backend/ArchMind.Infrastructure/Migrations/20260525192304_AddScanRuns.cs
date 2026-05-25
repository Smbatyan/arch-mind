using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScanRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scan_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    from_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    to_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    files_scanned = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    files_enqueued = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    graphify_nodes = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    graphify_edges = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    total_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    total_cost_usd = table.Column<decimal>(type: "numeric(10,6)", nullable: false, defaultValue: 0m),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_scan_runs_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scan_runs_workspace_repo_started_at_desc",
                table: "scan_runs",
                columns: new[] { "workspace_id", "repo_id", "started_at" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_runs");
        }
    }
}
