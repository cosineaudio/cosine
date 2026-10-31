using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackMusicBrainzSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FileModifiedAt",
                table: "Tracks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "Tracks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_MusicBrainzId",
                table: "Tracks",
                column: "MusicBrainzId");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_MusicBrainzId",
                table: "Artists",
                column: "MusicBrainzId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_MusicBrainzId",
                table: "Albums",
                column: "MusicBrainzId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_MusicBrainzId",
                table: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_Artists_MusicBrainzId",
                table: "Artists");

            migrationBuilder.DropIndex(
                name: "IX_Albums_MusicBrainzId",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "FileModifiedAt",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "Tracks");
        }
    }
}
