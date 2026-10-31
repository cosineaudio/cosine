using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

/// <summary>
/// SearchAsync ranks each entity type with ts_rank and produces ordered id lists, then hydrates
/// them with a `Where(id => ids.Contains(id))` query - which returns rows in whatever order
/// Postgres picks. All the ranking, including the deliberate 0.25 lyric weighting, used to be
/// discarded before the response.
/// </summary>
public class SearchServiceTests
{
    [Fact]
    public void OrderByIds_ReturnsRowsInRankedOrder_NotQueryOrder()
    {
        var ranked = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        // Hydration came back in a different order, as an unordered IN query may.
        var rows = new List<Artist>
        {
            new() { Id = ranked[2], Name = "third" },
            new() { Id = ranked[0], Name = "first" },
            new() { Id = ranked[1], Name = "second" }
        };

        var ordered = SearchService.OrderByIds(ranked, rows, a => a.Id);

        Assert.Equal(["first", "second", "third"], ordered.Select(a => a.Name));
    }

    [Fact]
    public void OrderByIds_IdWithNoHydratedRow_IsSkipped()
    {
        var present = Guid.NewGuid();
        var deletedSinceRanking = Guid.NewGuid();
        var rows = new List<Artist> { new() { Id = present, Name = "present" } };

        var ordered = SearchService.OrderByIds([deletedSinceRanking, present], rows, a => a.Id);

        Assert.Equal("present", Assert.Single(ordered).Name);
    }

    [Fact]
    public void OrderByIds_RepeatedId_EmitsTheRowOnce()
    {
        var id = Guid.NewGuid();
        var rows = new List<Artist> { new() { Id = id, Name = "once" } };

        var ordered = SearchService.OrderByIds([id, id], rows, a => a.Id);

        Assert.Single(ordered);
    }

    [Fact]
    public void OrderByIds_EmptyIdList_ReturnsNothing()
    {
        var rows = new List<Artist> { new() { Id = Guid.NewGuid(), Name = "orphan" } };

        Assert.Empty(SearchService.OrderByIds([], rows, a => a.Id));
    }
}
