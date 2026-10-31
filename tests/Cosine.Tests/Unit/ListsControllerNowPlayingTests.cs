using System.Security.Claims;
using Cosine.Api.Controllers;
using Cosine.Api.Generated;
using ArtistEntity = Cosine.Core.Entities.Artist;
using AlbumEntity = Cosine.Core.Entities.Album;
using TrackEntity = Cosine.Core.Entities.Track;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// getNowPlaying returned a hardcoded empty list with a comment claiming the feature lived
/// on ActivityHub, which nothing ever published to. HomePage renders this list.
/// </summary>
public class ListsControllerNowPlayingTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static (ListsController Controller, MediaAnnotationControllerTests.RecordingNowPlaying NowPlaying)
        Create(TestServiceProvider sp)
    {
        var nowPlaying = new MediaAnnotationControllerTests.RecordingNowPlaying();
        var controller = new ListsController(
            sp.Get<MediaLibraryService>(), sp.Get<PlayHistoryService>(), sp.Get<StarService>(),
            nowPlaying, new FakeRatingService());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return (controller, nowPlaying);
    }

    private static async Task<TrackEntity> SeedTrackAsync(TestServiceProvider sp)
    {
        var artist = new ArtistEntity { Id = Guid.NewGuid(), Name = "Test Artist", SortName = "test artist" };
        var album = new AlbumEntity
        {
            Id = Guid.NewGuid(), Name = "Test Album", SortName = "test album", ArtistId = artist.Id
        };
        var track = new TrackEntity
        {
            Id = Guid.NewGuid(), Title = "Track One", SortTitle = "track one",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = "test/one.mp3",
            TrackNumber = 1, DurationMs = 200000, FileSize = 5000000
        };

        sp.DbContext.Artists.Add(artist);
        sp.DbContext.Albums.Add(album);
        sp.DbContext.Tracks.Add(track);
        await sp.DbContext.SaveChangesAsync();
        return track;
    }

    private static List<NowPlayingEntry> EntriesOf(ActionResult<GetNowPlayingResponse> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<GetNowPlayingResponse>(ok.Value);
        var body = Assert.IsType<GetNowPlayingSuccessResponse>(response.SubsonicResponse);
        return body.NowPlaying!.Entry!.ToList();
    }

    [Fact]
    public async Task GetNowPlaying_ReturnsTheReportedTrack()
    {
        using var sp = new TestServiceProvider();
        var track = await SeedTrackAsync(sp);
        var (controller, nowPlaying) = Create(sp);

        await nowPlaying.ReportAsync(UserId, "alice", track.Id, positionMs: 42_000);

        var entry = Assert.Single(EntriesOf(await controller.GetNowPlaying()));
        Assert.Equal(track.Id.ToString(), entry.Id);
        Assert.Equal("Track One", entry.Title);
        Assert.Equal("Test Album", entry.Album);
        Assert.Equal("Test Artist", entry.Artist);
        Assert.Equal("alice", entry.Username);
        Assert.Equal(42_000, entry.PositionMs);
        Assert.Equal(NowPlayingEntryState.Playing, entry.State);
        Assert.Equal(0, entry.MinutesAgo);
    }

    [Fact]
    public async Task GetNowPlaying_OmitsOtherUsersPlayback()
    {
        using var sp = new TestServiceProvider();
        var track = await SeedTrackAsync(sp);
        var (controller, nowPlaying) = Create(sp);

        // Playback is private to each user: another account's session must never appear
        // on this user's dashboard, with or without a session of their own.
        var otherUser = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await nowPlaying.ReportAsync(otherUser, "bob", track.Id);

        Assert.Empty(EntriesOf(await controller.GetNowPlaying()));

        await nowPlaying.ReportAsync(UserId, "alice", track.Id);

        var entry = Assert.Single(EntriesOf(await controller.GetNowPlaying()));
        Assert.Equal("alice", entry.Username);
    }

    [Fact]
    public async Task GetNowPlaying_SkipsReportsWhoseTrackIsGone()
    {
        using var sp = new TestServiceProvider();
        var (controller, nowPlaying) = Create(sp);

        // A scan can remove the track between the report and the read.
        await nowPlaying.ReportAsync(UserId, "alice", Guid.NewGuid());

        Assert.Empty(EntriesOf(await controller.GetNowPlaying()));
    }

    [Fact]
    public async Task GetNowPlaying_NobodyListening_ReturnsAnEmptyList()
    {
        using var sp = new TestServiceProvider();
        var (controller, _) = Create(sp);

        Assert.Empty(EntriesOf(await controller.GetNowPlaying()));
    }
}
