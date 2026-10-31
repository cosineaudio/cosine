using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ListenPartyOneLivePerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ListenParties_OwnerUserId",
                table: "ListenParties");

            // Duplicates predate this constraint (two tabs racing "start" both passed the
            // existing-party check). Keep each owner's newest live party and end the rest,
            // or the unique index below cannot build.
            migrationBuilder.Sql("""
                UPDATE "ListenParties" p SET "EndedAt" = now()
                WHERE p."EndedAt" IS NULL
                  AND EXISTS (
                    SELECT 1 FROM "ListenParties" q
                    WHERE q."OwnerUserId" = p."OwnerUserId"
                      AND q."EndedAt" IS NULL
                      AND (q."CreatedAt", q."Id") > (p."CreatedAt", p."Id"));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ListenParties_OwnerUserId",
                table: "ListenParties",
                column: "OwnerUserId",
                unique: true,
                filter: "\"EndedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ListenParties_OwnerUserId",
                table: "ListenParties");

            migrationBuilder.CreateIndex(
                name: "IX_ListenParties_OwnerUserId",
                table: "ListenParties",
                column: "OwnerUserId");
        }
    }
}
