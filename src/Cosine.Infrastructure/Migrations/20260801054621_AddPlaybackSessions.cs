using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <summary>
    /// The durable half of remote playback to network renderers. Discovery, the connected
    /// target and the poll loop are all ephemeral and live in a singleton manager, but three
    /// things have to outlive the process. <c>PlaybackDevices</c> keeps the settings a user
    /// pinned to a renderer - its name, its transcode profile, its volume ceiling - keyed by
    /// (DriverId, DeviceKey) rather than by address, because a UDN or mDNS name survives a DHCP
    /// renewal and an IP does not. <c>PlaybackSessions</c> and <c>PlaybackSessionTracks</c> hold
    /// the queue and the position, so a restart can reconnect and resume, and so the Subsonic
    /// jukebox API has something to read. One session per device is enforced by the unique index
    /// on <c>DeviceId</c>; a device plays one thing at a time. Queue rows carry their own key
    /// rather than the composite (session, track) key used by <c>PlaylistTracks</c>, because a
    /// queue may hold the same track twice.
    /// </summary>
    public partial class AddPlaybackSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DriverId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeviceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TranscodeProfile = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MaxVolumePercent = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaybackSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CurrentIndex = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PositionMs = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    VolumePercent = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    RepeatMode = table.Column<int>(type: "integer", nullable: false),
                    Shuffle = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackSessions_PlaybackDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "PlaybackDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaybackSessions_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaybackSessionTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackSessionTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackSessionTracks_PlaybackSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "PlaybackSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaybackSessionTracks_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackDevices_DriverId_DeviceKey",
                table: "PlaybackDevices",
                columns: new[] { "DriverId", "DeviceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_DeviceId",
                table: "PlaybackSessions",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_OwnerUserId",
                table: "PlaybackSessions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessionTracks_SessionId_Position",
                table: "PlaybackSessionTracks",
                columns: new[] { "SessionId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessionTracks_TrackId",
                table: "PlaybackSessionTracks",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackSessionTracks");

            migrationBuilder.DropTable(
                name: "PlaybackSessions");

            migrationBuilder.DropTable(
                name: "PlaybackDevices");
        }
    }
}
