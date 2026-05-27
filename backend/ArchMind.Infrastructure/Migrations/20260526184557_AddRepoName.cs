using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchMind.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepoName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "repos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Backfill: derive repo name from the trailing path segment of
            // github_url, stripping a trailing ".git" if present.
            // Example: https://github.com/Smbatyan/blot-stars → blot-stars
            migrationBuilder.Sql(@"
                UPDATE repos
                SET name = LEFT(
                    regexp_replace(
                        regexp_replace(
                            split_part(rtrim(github_url, '/'), '/', -1),
                            '\.git$', ''
                        ),
                        '\?.*$', ''
                    ),
                    200
                )
                WHERE name = '' OR name IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "name",
                table: "repos");
        }
    }
}
