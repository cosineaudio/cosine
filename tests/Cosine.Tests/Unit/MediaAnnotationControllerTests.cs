using System.Security.Claims;
using Cosine.Api.Controllers;
using Cosine.Api.Middleware;
using Cosine.Api.Generated;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// Play counts were inflated by design: reportPlayback recorded a play on every
/// State.Playing event, so each unpause counted again - and every non-zero rating starred
/// the track, so rating something 1 added it to the user's favourites.
/// <para>
/// Ratings are now stored in their own table and no longer touch stars at all (#29), which
/// is the behaviour change the SetRating group below pins.
/// </para>
/// </summary>
public class MediaAnnotationControllerTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TrackId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static (MediaAnnotationController Controller, RecordingPlayHistory Plays, RecordingStars Stars, RecordingNowPlaying NowPlaying, FakeRatingService Ratings) Create()
    {
        var plays = new RecordingPlayHistory();
        var stars = new RecordingStars();
        var nowPlaying = new RecordingNowPlaying();
        var ratings = new FakeRatingService();
        var controller = new MediaAnnotationController(stars, ratings, plays, nowPlaying);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString()), new Claim(ClaimTypes.Name, "alice")],
            "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return (controller, plays, stars, nowPlaying, ratings);
    }

    [Theory]
    [InlineData(State.Playing)]
    [InlineData(State.Starting)]
    [InlineData(State.Paused)]
    public async Task ReportPlayback_DoesNotRecordAPlay(State state)
    {
        var (controller, plays, _, nowPlaying, _) = Create();

        await controller.ReportPlayback(TrackId.ToString(), MediaType4.Song, positionMs: 30_000, state);

        Assert.Empty(plays.Recorded);
        Assert.Equal(TrackId, Assert.Single(nowPlaying.Reports).TrackId);
    }

    [Fact]
    public async Task ReportPlayback_RepeatedUnpauses_StillRecordNoPlays()
    {
        var (controller, plays, _, _, _) = Create();

        for (var i = 0; i < 5; i++)
            await controller.ReportPlayback(TrackId.ToString(), MediaType4.Song, i * 1000, State.Playing);

        Assert.Empty(plays.Recorded);
    }

    [Fact]
    public async Task ReportPlayback_Stopped_ReportsTheStoppedState()
    {
        var (controller, _, _, nowPlaying, _) = Create();

        await controller.ReportPlayback(TrackId.ToString(), MediaType4.Song, 0, State.Stopped);

        Assert.Equal(INowPlayingService.StateStopped, Assert.Single(nowPlaying.Reports).State);
    }

    [Fact]
    public async Task Scrobble_Submission_RecordsThePlay()
    {
        var (controller, plays, _, nowPlaying, _) = Create();

        await controller.Scrobble(TrackId.ToString(), submission: true);

        Assert.Equal((UserId, TrackId), Assert.Single(plays.Recorded));
        // A submission still means the user is listening to it.
        Assert.Equal(TrackId, Assert.Single(nowPlaying.Reports).TrackId);
    }

    [Fact]
    public async Task Scrobble_NowPlayingNotification_RecordsNoPlay()
    {
        var (controller, plays, _, nowPlaying, _) = Create();

        await controller.Scrobble(TrackId.ToString(), submission: false);

        Assert.Empty(plays.Recorded);
        Assert.Single(nowPlaying.Reports);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SetRating_StoresTheValueAndLeavesStarsAlone(int rating)
    {
        var (controller, _, stars, _, ratings) = Create();

        await controller.SetRating(TrackId.ToString(), rating);

        Assert.Equal((TrackId, rating), Assert.Single(ratings.Calls));
        // The old behaviour starred on 4-5 and unstarred on 1-3, conflating two independent
        // Subsonic concepts because the schema could not carry a rating.
        Assert.Empty(stars.Calls);
    }

    [Fact]
    public async Task SetRating_Zero_ClearsTheRatingWithoutTouchingStars()
    {
        var (controller, _, stars, _, ratings) = Create();

        await controller.SetRating(TrackId.ToString(), 0);

        Assert.Equal((TrackId, 0), Assert.Single(ratings.Calls));
        Assert.Empty(stars.Calls);
    }

    [Fact]
    public async Task SetRating_UnknownId_IsNotFound()
    {
        var (controller, _, _, _, ratings) = Create();
        ratings.Resolves = false;

        var ex = await Assert.ThrowsAsync<SubsonicException>(() =>
            controller.SetRating(Guid.NewGuid().ToString(), 3));

        Assert.Equal(SubsonicErrorCode._70, ex.ErrorCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public async Task SetRating_OutOfRange_IsRejected(int rating)
    {
        var (controller, _, _, _, ratings) = Create();

        await Assert.ThrowsAsync<SubsonicException>(() =>
            controller.SetRating(TrackId.ToString(), rating));

        Assert.Empty(ratings.Calls);
    }

    private sealed class RecordingPlayHistory : IPlayHistoryService
    {
        public List<(Guid UserId, Guid TrackId)> Recorded { get; } = [];

        public Task RecordPlayAsync(Guid userId, Guid trackId, CancellationToken ct = default)
        {
            Recorded.Add((userId, trackId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Track>> GetRecentPlaysAsync(Guid userId, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Track>>([]);

        public Task<IReadOnlyList<(Track Track, int Count)>> GetMostPlayedAsync(Guid userId, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(Track, int)>>([]);

        public Task<int> CleanupAsync(TimeSpan retention, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class RecordingStars : IStarService
    {
        public List<(string Action, string ItemType, Guid ItemId)> Calls { get; } = [];

        public Task<StarredItems> GetStarredAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(new StarredItems([], [], []));

        public Task<IReadOnlyList<Core.Entities.Album>> GetStarredAlbumsAsync(Guid userId, int offset, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Core.Entities.Album>>([]);

        public Task StarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
        {
            Calls.Add(("star", itemType, itemId));
            return Task.CompletedTask;
        }

        public Task UnstarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
        {
            Calls.Add(("unstar", itemType, itemId));
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingNowPlaying : INowPlayingService
    {
        public List<NowPlayingReport> Reports { get; } = [];

        public Task ReportAsync(
            Guid userId, string username, Guid trackId,
            int? positionMs = null, string state = INowPlayingService.StatePlaying,
            CancellationToken ct = default)
        {
            Reports.Add(new NowPlayingReport(userId, username, trackId, positionMs, state, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public IReadOnlyList<NowPlayingReport> GetCurrent() => Reports;
    }
}
