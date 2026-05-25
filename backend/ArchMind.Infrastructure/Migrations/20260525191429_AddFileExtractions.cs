using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFileExtractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_extractions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    extraction_payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_extractions", x => x.id);
                    table.ForeignKey(
                        name: "FK_file_extractions_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_file_extractions_content_hash",
                table: "file_extractions",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "IX_file_extractions_workspace_id_repo_id_file_path",
                table: "file_extractions",
                columns: new[] { "workspace_id", "repo_id", "file_path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_extractions");
        }
    }
}
