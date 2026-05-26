using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClarifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clarifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    context = table.Column<string>(type: "text", nullable: true),
                    choices = table.Column<string[]>(type: "text[]", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Open"),
                    answer = table.Column<string>(type: "text", nullable: true),
                    answered_by_user_id = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    related_file_paths = table.Column<string[]>(type: "text[]", nullable: false),
                    related_node_names = table.Column<string[]>(type: "text[]", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clarifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_clarifications_repos_repo_id",
                        column: x => x.repo_id,
                        principalTable: "repos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_clarifications_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_clarifications_repo_id",
                table: "clarifications",
                column: "repo_id");

            migrationBuilder.CreateIndex(
                name: "ix_clarifications_workspace_fingerprint",
                table: "clarifications",
                columns: new[] { "workspace_id", "fingerprint" },
                unique: true,
                filter: "fingerprint IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_clarifications_workspace_status_priority_desc",
                table: "clarifications",
                columns: new[] { "workspace_id", "status", "priority" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clarifications");
        }
    }
}
