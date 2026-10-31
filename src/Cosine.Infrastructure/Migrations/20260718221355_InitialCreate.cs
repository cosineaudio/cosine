using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Artists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SortName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MusicBrainzId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImageBlob = table.Column<byte[]>(type: "bytea", nullable: true),
                    Biography = table.Column<string>(type: "text", nullable: true),
                    StarCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Genres",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Genres", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InternetRadioStations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StreamUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    HomepageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternetRadioStations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FilesAdded = table.Column<int>(type: "integer", nullable: false),
                    FilesRemoved = table.Column<int>(type: "integer", nullable: false),
                    FilesUpdated = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchIndex",
                columns: table => new
                {
                    EntityType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TextVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false),
                    LyricsVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchIndex", x => new { x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    SubsonicSecret = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Albums",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SortName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    MusicBrainzId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CoverBlob = table.Column<byte[]>(type: "bytea", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TrackCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    StarCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Albums", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Albums_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ArtistGenres",
                columns: table => new
                {
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenreId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistGenres", x => new { x.ArtistId, x.GenreId });
                    table.ForeignKey(
                        name: "FK_ArtistGenres_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtistGenres_Genres_GenreId",
                        column: x => x.GenreId,
                        principalTable: "Genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    TrackCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Playlists_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayQueues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentIndex = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PositionMs = table.Column<long>(type: "bigint", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayQueues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayQueues_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VisitCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shares_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Stars",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stars", x => new { x.UserId, x.ItemType, x.ItemId });
                    table.ForeignKey(
                        name: "FK_Stars_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlbumGenres",
                columns: table => new
                {
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenreId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumGenres", x => new { x.AlbumId, x.GenreId });
                    table.ForeignKey(
                        name: "FK_AlbumGenres_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlbumGenres_Genres_GenreId",
                        column: x => x.GenreId,
                        principalTable: "Genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SortTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackNumber = table.Column<int>(type: "integer", nullable: true),
                    DiscNumber = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    FilePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    BitRate = table.Column<int>(type: "integer", nullable: true),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    StarCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tracks_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bookmarks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionMs = table.Column<long>(type: "bigint", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookmarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookmarks_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Bookmarks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayHistory_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlayHistory_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistTracks",
                columns: table => new
                {
                    PlaylistId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistTracks", x => new { x.PlaylistId, x.TrackId });
                    table.ForeignKey(
                        name: "FK_PlaylistTracks_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistTracks_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayQueueTracks",
                columns: table => new
                {
                    PlayQueueId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayQueueTracks", x => new { x.PlayQueueId, x.TrackId });
                    table.ForeignKey(
                        name: "FK_PlayQueueTracks_PlayQueues_PlayQueueId",
                        column: x => x.PlayQueueId,
                        principalTable: "PlayQueues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlayQueueTracks_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackArtists",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackArtists", x => new { x.TrackId, x.ArtistId });
                    table.ForeignKey(
                        name: "FK_TrackArtists_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackArtists_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackGenres",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenreId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackGenres", x => new { x.TrackId, x.GenreId });
                    table.ForeignKey(
                        name: "FK_TrackGenres_Genres_GenreId",
                        column: x => x.GenreId,
                        principalTable: "Genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackGenres_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TranscodeCache",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BitRate = table.Column<int>(type: "integer", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAccessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscodeCache", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranscodeCache_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlbumGenres_GenreId",
                table: "AlbumGenres",
                column: "GenreId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_ArtistId",
                table: "Albums",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_CreatedAt",
                table: "Albums",
                column: "CreatedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Albums_SortName",
                table: "Albums",
                column: "SortName");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistGenres_GenreId",
                table: "ArtistGenres",
                column: "GenreId");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_SortName",
                table: "Artists",
                column: "SortName");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_TrackId",
                table: "Bookmarks",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_UserId",
                table: "Bookmarks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Genres_Name",
                table: "Genres",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayHistory_TrackId",
                table: "PlayHistory",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayHistory_UserId_PlayedAt",
                table: "PlayHistory",
                columns: new[] { "UserId", "PlayedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_OwnerId",
                table: "Playlists",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTracks_TrackId",
                table: "PlaylistTracks",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayQueues_UserId",
                table: "PlayQueues",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayQueueTracks_TrackId",
                table: "PlayQueueTracks",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchIndex_TextVector",
                table: "SearchIndex",
                column: "TextVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_Shares_Token",
                table: "Shares",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shares_UserId",
                table: "Shares",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Stars_UserId_ItemType_ItemId",
                table: "Stars",
                columns: new[] { "UserId", "ItemType", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackArtists_ArtistId",
                table: "TrackArtists",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackGenres_GenreId",
                table: "TrackGenres",
                column: "GenreId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_AlbumId",
                table: "Tracks",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_ArtistId",
                table: "Tracks",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_CreatedAt",
                table: "Tracks",
                column: "CreatedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_FilePath",
                table: "Tracks",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_SortTitle",
                table: "Tracks",
                column: "SortTitle");

            migrationBuilder.CreateIndex(
                name: "IX_TranscodeCache_TrackId_Format_BitRate",
                table: "TranscodeCache",
                columns: new[] { "TrackId", "Format", "BitRate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumGenres");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ArtistGenres");

            migrationBuilder.DropTable(
                name: "Bookmarks");

            migrationBuilder.DropTable(
                name: "InternetRadioStations");

            migrationBuilder.DropTable(
                name: "PlayHistory");

            migrationBuilder.DropTable(
                name: "PlaylistTracks");

            migrationBuilder.DropTable(
                name: "PlayQueueTracks");

            migrationBuilder.DropTable(
                name: "ScanLogs");

            migrationBuilder.DropTable(
                name: "SearchIndex");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Shares");

            migrationBuilder.DropTable(
                name: "Stars");

            migrationBuilder.DropTable(
                name: "TrackArtists");

            migrationBuilder.DropTable(
                name: "TrackGenres");

            migrationBuilder.DropTable(
                name: "TranscodeCache");

            migrationBuilder.DropTable(
                name: "Playlists");

            migrationBuilder.DropTable(
                name: "PlayQueues");

            migrationBuilder.DropTable(
                name: "Genres");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Albums");

            migrationBuilder.DropTable(
                name: "Artists");
        }
    }
}
