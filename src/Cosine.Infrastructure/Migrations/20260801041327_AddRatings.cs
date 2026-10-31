using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <summary>
    /// Per-user 1–5 ratings. Ratings were previously discarded - <c>setRating</c> projected them
    /// onto the star, so <c>userRating</c> never reached a client. A rating belongs to a
    /// (user, item) pair, so this is a join table with exactly the shape of <c>Stars</c>: the
    /// same composite key, the same 16-char polymorphic discriminator, and a cascade only from
    /// <c>Users</c>. As with <c>Stars</c>, <c>ItemId</c> has no foreign key, so deleting a track
    /// leaves its rating rows behind.
    /// </summary>
    public partial class AddRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Ratings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ratings", x => new { x.UserId, x.ItemType, x.ItemId });
                    table.ForeignKey(
                        name: "FK_Ratings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_UserId_ItemType_ItemId",
                table: "Ratings",
                columns: new[] { "UserId", "ItemType", "ItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ratings");
        }
    }
}
