using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <summary>
    /// Removes the sharing feature's table. Sharing was never reachable - the URLs it handed
    /// out pointed at a <c>getShare</c> endpoint that did not exist in the spec or the code, and
    /// the web UI never referenced it - so any rows here are unusable regardless. Unauthenticated
    /// streaming is deliberately out of scope for a self-hosted instance.
    /// <para>
    /// This drops data. <c>Down</c> restores the schema but not the rows.
    /// </para>
    /// </summary>
    public partial class DropShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Shares");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VisitCount = table.Column<int>(type: "integer", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_Shares_Token",
                table: "Shares",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shares_UserId",
                table: "Shares",
                column: "UserId");
        }
    }
}
