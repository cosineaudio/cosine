using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddListenParty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ListenParties",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListenParties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListenParties_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ListenPartyMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListenPartyMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListenPartyMembers_ListenParties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "ListenParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListenPartyMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ListenPartyTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedByMemberId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListenPartyTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListenPartyTracks_ListenParties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "ListenParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListenPartyTracks_ListenPartyMembers_AddedByMemberId",
                        column: x => x.AddedByMemberId,
                        principalTable: "ListenPartyMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ListenPartyTracks_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListenParties_EndedAt_LastActivityAt",
                table: "ListenParties",
                columns: new[] { "EndedAt", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ListenParties_OwnerUserId",
                table: "ListenParties",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ListenParties_Token",
                table: "ListenParties",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListenPartyMembers_PartyId",
                table: "ListenPartyMembers",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_ListenPartyMembers_UserId",
                table: "ListenPartyMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ListenPartyTracks_AddedByMemberId",
                table: "ListenPartyTracks",
                column: "AddedByMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ListenPartyTracks_PartyId_Position",
                table: "ListenPartyTracks",
                columns: new[] { "PartyId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ListenPartyTracks_TrackId",
                table: "ListenPartyTracks",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListenPartyTracks");

            migrationBuilder.DropTable(
                name: "ListenPartyMembers");

            migrationBuilder.DropTable(
                name: "ListenParties");
        }
    }
}
