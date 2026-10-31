using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Unit;

/// <summary>
/// #44: the shared-genre candidate pool used to be every artist tagged with any of the seed's
/// genres - one common genre ("Rock") made that the whole artist table, and the follow-up query
/// then pulled a row per (artist, genre) pair for all of them into memory. The cap only helps if
/// it reaches SQL, and the in-memory provider runs the LINQ as written either way, so this
/// asserts the translation: EF needs only a connection *string* to generate SQL, never a live
/// server (see <see cref="RankedQuerySqlTests"/>).
/// </summary>
public class SimilarityQuerySqlTests
{
    private static CosineDbContext NpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<CosineDbContext>()
            .UseNpgsql("Host=localhost;Database=cosine;Username=none;Password=none", o => o.UseVector())
            .Options;
        return new CosineDbContext(options);
    }

    [Fact]
    public void SharedGenreCandidates_RanksAndCapsThePoolInSql()
    {
        using var db = NpgsqlContext();

        var sql = SimilarityService.SharedGenreCandidatesQuery(
            db.TrackGenres, Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], 500).ToQueryString();

        // GROUP BY "ArtistId" ORDER BY count(*) DESC LIMIT 500 - the server ranks and truncates,
        // so at most 500 ids ever cross the wire however common the seed's genres are.
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("ORDER BY count(", sql);
        Assert.Contains("DESC", sql);
        Assert.Contains("LIMIT", sql);
        // The DISTINCT belongs to the inner (artist, genre) pair projection, which carries no
        // ordering of its own - not a repeat of #32's silently-dropped ORDER BY.
        Assert.DoesNotContain("DISTINCT", sql[sql.IndexOf("GROUP BY", StringComparison.Ordinal)..]);
    }
}
