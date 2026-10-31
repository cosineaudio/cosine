using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistEntryId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "PlaylistTracks",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTracks_Id",
                table: "PlaylistTracks",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlaylistTracks_Id",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "PlaylistTracks");
        }
    }
}
