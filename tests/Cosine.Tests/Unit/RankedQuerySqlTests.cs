using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Unit;

/// <summary>
/// Pins the SQL EF generates for the two "top-N by rank/recency" queries.
/// </summary>
/// <remarks>
/// Both used to end in <c>.Select(id).Distinct().Take(n)</c>, which EF translates to
/// <c>SELECT DISTINCT ... LIMIT n</c> - it drops the <c>ORDER BY</c> entirely rather than
/// emitting the illegal <c>SELECT DISTINCT x ORDER BY y</c> Postgres would reject. Nothing
/// threw, so search ranking and "recently played" recency were silently thrown away.
/// The unit suites can't catch this (the in-memory provider runs the LINQ as written) and the
/// integration suite needs Docker, so these assert the translation itself: EF only needs a
/// connection *string* to produce SQL, never a live server.
/// </remarks>
public class RankedQuerySqlTests
{
    private static CosineDbContext NpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<CosineDbContext>()
            .UseNpgsql("Host=localhost;Database=cosine;Username=none;Password=none", o => o.UseVector())
            .Options;
        return new CosineDbContext(options);
    }

    [Theory]
    [InlineData("artist")]
    [InlineData("album")]
    [InlineData("track")]
    public void RankedIdsQuery_OrdersByRankInSql(string entityType)
    {
        using var db = NpgsqlContext();

        var sql = SearchService.RankedIdsQuery(db.SearchIndex, entityType, "hello world", 20).ToQueryString();

        Assert.Contains("ORDER BY", sql);
        Assert.Contains("ts_rank", sql);
        Assert.Contains("LIMIT", sql);
        // A DISTINCT would take the ordering with it; the (EntityType, EntityId) primary key
        // already guarantees one row per id, so there must not be one.
        Assert.DoesNotContain("DISTINCT", sql);
    }

    [Fact]
    public void RankedIdsQuery_ForTracks_FoldsInTheLyricRank()
    {
        using var db = NpgsqlContext();

        var sql = SearchService.RankedIdsQuery(db.SearchIndex, "track", "hello world", 20).ToQueryString();

        // The lyric rank is weighted at 0.25 so a metadata hit always outranks a lyric-only hit.
        Assert.Contains("0.25", sql);
        Assert.Contains("LyricsVector", sql);
    }

    // #43: the full rebuild no longer truncates SearchIndex up front, so stale rows are swept
    // afterwards instead. That sweep has to be a single NOT IN (subquery) - pulling every live
    // id into the app to build the exclusion list would be its own load-everything query, and an
    // untranslatable expression would only surface at runtime (the integration suite is #59).
    [Fact]
    public void OrphanEntriesQuery_IsOneNotInSubquery()
    {
        using var db = NpgsqlContext();

        var sql = SearchService
            .OrphanEntriesQuery(db.SearchIndex, "artist", db.Artists.Select(a => a.Id))
            .ToQueryString();

        // …WHERE "EntityType" = @type AND "EntityId" NOT IN (SELECT "Id" FROM "Artists")
        Assert.Contains("NOT IN (", sql);
        Assert.Contains("FROM \"Artists\"", sql);
    }

    [Fact]
    public void RecentTrackIdsQuery_OrdersByLatestPlayInSql()
    {
        using var db = NpgsqlContext();

        var sql = PlayHistoryService.RecentTrackIdsQuery(db.PlayHistory, Guid.NewGuid(), 50).ToQueryString();

        Assert.Contains("GROUP BY", sql);
        Assert.Contains("max(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("DESC", sql);
        Assert.DoesNotContain("DISTINCT", sql);
    }
}
