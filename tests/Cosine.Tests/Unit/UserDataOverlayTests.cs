using Cosine.Api.WebApi;
using Cosine.Api.WebApi.Dtos;
using Cosine.Core.Interfaces;

namespace Cosine.Tests.Unit;

/// <summary>
/// The web-API twin of <c>RatingOverlayTests</c>: per-user ratings are joined onto responses
/// *after* mapping because the mapped entities come from a cache shared across users. The
/// overlay must batch (one lookup per item type) and must not query when nothing was collected.
/// </summary>
public class UserDataOverlayTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static TrackDto Track() => new() { Id = Guid.NewGuid() };
    private static AlbumSummaryDto Album() => new() { Id = Guid.NewGuid() };

    [Fact]
    public async Task StampsRatingsOntoTracksAndAlbums()
    {
        var track = Track();
        var album = Album();
        var ratings = new FakeRatingService()
            .Seed(IRatingService.Track, track.Id, 5)
            .Seed(IRatingService.Album, album.Id, 3);

        await new UserDataOverlay().Add(track).Add(album).ApplyAsync(ratings, UserId);

        Assert.Equal(5, track.UserRating);
        Assert.Equal(3, album.UserRating);
    }

    [Fact]
    public async Task LeavesUnratedItemsNull()
    {
        var track = Track();

        await new UserDataOverlay().Add(track).ApplyAsync(new FakeRatingService(), UserId);

        Assert.Null(track.UserRating);
    }

    [Fact]
    public async Task IssuesOneLookupPerItemTypeHoweverManyNodes()
    {
        var ratings = new FakeRatingService();
        var overlay = new UserDataOverlay()
            .AddRange([Track(), Track(), Track()])
            .AddRange([Album(), Album()]);

        await overlay.ApplyAsync(ratings, UserId);

        Assert.Equal(2, ratings.Lookups.Count);
        Assert.Contains(ratings.Lookups, l => l.ItemType == IRatingService.Track && l.Ids.Count == 3);
        Assert.Contains(ratings.Lookups, l => l.ItemType == IRatingService.Album && l.Ids.Count == 2);
    }

    [Fact]
    public async Task QueriesNothingWhenNothingWasCollected()
    {
        var ratings = new FakeRatingService();

        await new UserDataOverlay().ApplyAsync(ratings, UserId);
        await new UserDataOverlay().AddRange(Array.Empty<TrackDto>()).ApplyAsync(ratings, UserId);

        Assert.Empty(ratings.Lookups);
    }

    [Fact]
    public async Task StampsEveryNodeCarryingTheSameId()
    {
        // The same track can appear twice in one response (a radio queue with a repeat);
        // both nodes must be stamped even though the id is looked up once.
        var id = Guid.NewGuid();
        var first = new TrackDto { Id = id };
        var second = new TrackDto { Id = id };
        var ratings = new FakeRatingService().Seed(IRatingService.Track, id, 4);

        await new UserDataOverlay().Add(first).Add(second).ApplyAsync(ratings, UserId);

        Assert.Equal(4, first.UserRating);
        Assert.Equal(4, second.UserRating);
        var lookup = Assert.Single(ratings.Lookups);
        Assert.Single(lookup.Ids.Distinct());
    }
}
