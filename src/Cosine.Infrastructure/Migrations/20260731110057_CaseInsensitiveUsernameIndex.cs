using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <summary>
    /// Authentication resolves users case-insensitively (<c>lower("Username")</c>) while the
    /// unique index was case-sensitive, so "Admin" and "admin" could both exist and every
    /// login resolved to whichever row the planner returned first - an account-takeover
    /// primitive. Expression indexes have no EF model representation, hence the raw SQL.
    ///
    /// The statement fails if a database already holds usernames differing only by case.
    /// That is deliberate: those rows have to be renamed by hand, and skipping the index
    /// would leave the flaw in place.
    /// </summary>
    public partial class CaseInsensitiveUsernameIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """CREATE UNIQUE INDEX "IX_Users_Username_Lower" ON "Users" (lower("Username"));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_Users_Username_Lower";""");
        }
    }
}
