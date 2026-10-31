using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLyrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Lyrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Lang = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Synced = table.Column<bool>(type: "boolean", nullable: false),
                    OffsetMs = table.Column<int>(type: "integer", nullable: true),
                    DisplayArtist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DisplayTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LinesJson = table.Column<string>(type: "jsonb", nullable: false),
                    PlainText = table.Column<string>(type: "text", nullable: false),
                    SourceSize = table.Column<long>(type: "bigint", nullable: true),
                    SourceModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lyrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Lyrics_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lyrics_SourcePath",
                table: "Lyrics",
                column: "SourcePath");

            migrationBuilder.CreateIndex(
                name: "IX_Lyrics_TrackId",
                table: "Lyrics",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Lyrics");
        }
    }
}
