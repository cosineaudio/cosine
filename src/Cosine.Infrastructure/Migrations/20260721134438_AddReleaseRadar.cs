using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseRadar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Playlists_OwnerId",
                table: "Playlists");

            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "Playlists",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseType",
                table: "Albums",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_OwnerId_IsSystem",
                table: "Playlists",
                columns: new[] { "OwnerId", "IsSystem" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Playlists_OwnerId_IsSystem",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "ReleaseType",
                table: "Albums");

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_OwnerId",
                table: "Playlists",
                column: "OwnerId");
        }
    }
}
