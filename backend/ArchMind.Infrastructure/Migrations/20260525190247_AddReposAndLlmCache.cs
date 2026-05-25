using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReposAndLlmCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_extraction_cache",
                columns: table => new
                {
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    hit_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_extraction_cache", x => x.content_hash);
                    table.ForeignKey(
                        name: "FK_llm_extraction_cache_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "repos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    default_branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: "main"),
                    last_processed_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    working_dir_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    pat_token = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "pending"),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repos", x => x.id);
                    table.ForeignKey(
                        name: "FK_repos_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_extraction_cache_workspace_id",
                table: "llm_extraction_cache",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "IX_repos_workspace_id",
                table: "repos",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_extraction_cache");

            migrationBuilder.DropTable(
                name: "repos");
        }
    }
}
