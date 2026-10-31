using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStatSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CacheHits = table.Column<long>(type: "bigint", nullable: false),
                    CacheMisses = table.Column<long>(type: "bigint", nullable: false),
                    StreamRequests = table.Column<long>(type: "bigint", nullable: false),
                    TranscodeCount = table.Column<long>(type: "bigint", nullable: false),
                    TranscodeCacheHits = table.Column<long>(type: "bigint", nullable: false),
                    DirectPlayCount = table.Column<long>(type: "bigint", nullable: false),
                    PlaysRecorded = table.Column<long>(type: "bigint", nullable: false),
                    TtfbP50Ms = table.Column<double>(type: "double precision", nullable: false),
                    TtfbP95Ms = table.Column<double>(type: "double precision", nullable: false),
                    RedisKeyCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatSnapshots_CapturedAt",
                table: "StatSnapshots",
                column: "CapturedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatSnapshots");
        }
    }
}
