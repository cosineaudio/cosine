using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackArtistCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzRecordingId",
                table: "Tracks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreditedAs",
                table: "TrackArtists",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "JoinPhrase",
                table: "TrackArtists",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Ordinal",
                table: "TrackArtists",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "TrackArtists",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "tag");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_MusicBrainzRecordingId",
                table: "Tracks",
                column: "MusicBrainzRecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackArtists_TrackId_Ordinal",
                table: "TrackArtists",
                columns: new[] { "TrackId", "Ordinal" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_MusicBrainzRecordingId",
                table: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_TrackArtists_TrackId_Ordinal",
                table: "TrackArtists");

            migrationBuilder.DropColumn(
                name: "MusicBrainzRecordingId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "CreditedAs",
                table: "TrackArtists");

            migrationBuilder.DropColumn(
                name: "JoinPhrase",
                table: "TrackArtists");

            migrationBuilder.DropColumn(
                name: "Ordinal",
                table: "TrackArtists");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "TrackArtists");
        }
    }
}
