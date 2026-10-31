using System.Security.Claims;
using Cosine.Api.WebApi.Controllers;
using Cosine.Api.WebApi.Dtos;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// The web API's mutation surface. The contracts pinned here are the ones that replace
/// Subsonic warts: playlist removal by entry id (not position index), queue save via JSON
/// body with the current track named by id, play reporting that only counts a play on the
/// deliberate countPlay signal, and the ownership/system-playlist gates carried over intact.
/// </summary>
public class WebApiMutationControllersTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static T Authenticated<T>(T controller, Guid? userId = null, bool admin = false)
        where T : ControllerBase
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (userId ?? UserId).ToString()),
            new(ClaimTypes.Name, "alice"),
        };
        if (admin) claims.Add(new Claim("IsAdmin", "True"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    private sealed record Seeded(Artist Artist, Album Album, List<Track> Tracks);

    private static Seeded Seed(Cosine.Infrastructure.Data.CosineDbContext db, int trackCount = 3)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Main", SortName = "main" };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        var tracks = new List<Track>();
        for (var i = 1; i <= trackCount; i++)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", SortTitle = $"song {i}",
                AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{i}.flac",
                TrackNumber = i, DurationMs = 60_000,
            };
            db.Tracks.Add(track);
            tracks.Add(track);
        }
        // A user row for playlist/queue FKs.
        db.Users.Add(new User { Id = UserId, Username = "alice", PasswordHash = "x" });
        return new Seeded(artist, album, tracks);
    }

    private static Api.WebApi.Controllers.PlaylistsController Playlists(
        TestServiceProvider sp, Guid? userId = null, bool admin = false)
        => Authenticated(
            new Api.WebApi.Controllers.PlaylistsController(sp.Get<PlaylistService>(), new FakeRatingService()),
            userId, admin);

    // --- Playlists ---

    [Fact]
    public async Task Playlist_CreateWithTracks_ReturnsDetailWithEntryIds()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        await sp.DbContext.SaveChangesAsync();

        var result = await Playlists(sp).Create(
            new CreatePlaylistRequest("Mix", lib.Tracks.Select(t => t.Id).ToList()), default);

        var created = Assert.IsType<CreatedAtRouteResult>(result.Result);
        var dto = Assert.IsType<PlaylistDetailDto>(created.Value);
        Assert.Equal(3, dto.Entries.Count);
        Assert.All(dto.Entries, e => Assert.NotEqual(Guid.Empty, e.EntryId));
        // Entry ids are per-entry identities, not the track ids.
        Assert.Distinct(dto.Entries.Select(e => e.EntryId));
    }

    [Fact]
    public async Task Playlist_RemoveByEntryId_RemovesExactlyThatEntry()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        await sp.DbContext.SaveChangesAsync();

        var controller = Playlists(sp);
        var created = (PlaylistDetailDto)((CreatedAtRouteResult)(await controller.Create(
            new CreatePlaylistRequest("Mix", lib.Tracks.Select(t => t.Id).ToList()), default)).Result!).Value!;
        var middle = created.Entries[1];

        var remove = await controller.RemoveEntry(created.Id, middle.EntryId, default);
        Assert.IsType<NoContentResult>(remove);

        var detail = (await controller.GetPlaylist(created.Id, default)).Value!;
        Assert.Equal(2, detail.Entries.Count);
        Assert.DoesNotContain(detail.Entries, e => e.EntryId == middle.EntryId);
        Assert.Equal(2, detail.TrackCount);
    }

    [Fact]
    public async Task Playlist_RemoveEntry_ScopedToThePlaylist()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        await sp.DbContext.SaveChangesAsync();

        var controller = Playlists(sp);
        var first = (PlaylistDetailDto)((CreatedAtRouteResult)(await controller.Create(
            new CreatePlaylistRequest("A", [lib.Tracks[0].Id]), default)).Result!).Value!;
        var second = (PlaylistDetailDto)((CreatedAtRouteResult)(await controller.Create(
            new CreatePlaylistRequest("B", [lib.Tracks[1].Id]), default)).Result!).Value!;

        // A leaked entry id from playlist B must not delete anything from playlist A.
        await controller.RemoveEntry(first.Id, second.Entries[0].EntryId, default);

        Assert.Single((await controller.GetPlaylist(first.Id, default)).Value!.Entries);
        Assert.Single((await controller.GetPlaylist(second.Id, default)).Value!.Entries);
    }

    [Fact]
    public async Task Playlist_ClearTracks_EmptiesButKeepsThePlaylist()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        await sp.DbContext.SaveChangesAsync();

        var controller = Playlists(sp);
        var created = (PlaylistDetailDto)((CreatedAtRouteResult)(await controller.Create(
            new CreatePlaylistRequest("Mix", lib.Tracks.Select(t => t.Id).ToList()), default)).Result!).Value!;

        await controller.ClearTracks(created.Id, default);

        var detail = (await controller.GetPlaylist(created.Id, default)).Value!;
        Assert.Empty(detail.Entries);
        Assert.Equal(0, detail.TrackCount);
        Assert.Equal(0, detail.DurationMs);
    }

    [Fact]
    public async Task Playlist_NonOwnerCannotEdit_AdminCanOnlyDelete()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        var stranger = Guid.NewGuid();
        sp.DbContext.Users.Add(new User { Id = stranger, Username = "bob", PasswordHash = "x" });
        await sp.DbContext.SaveChangesAsync();

        var owner = Playlists(sp);
        var created = (PlaylistDetailDto)((CreatedAtRouteResult)(await owner.Create(
            new CreatePlaylistRequest("Mine", [lib.Tracks[0].Id]), default)).Result!).Value!;

        var strangerController = Playlists(sp, stranger);
        Assert.IsType<ObjectResult>(
            await strangerController.Update(created.Id, new UpdatePlaylistRequest("Stolen"), default));
        Assert.IsType<ObjectResult>(await strangerController.Delete(created.Id, default));

        var admin = Playlists(sp, stranger, admin: true);
        Assert.IsType<NoContentResult>(await admin.Delete(created.Id, default));
    }

    [Fact]
    public async Task Playlist_SystemPlaylistIsReadOnly()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        Seed(db);
        var system = new Playlist
        {
            Id = Guid.NewGuid(), Name = "Release Radar", OwnerId = UserId, IsSystem = true
        };
        db.Playlists.Add(system);
        await db.SaveChangesAsync();

        var controller = Playlists(sp);

        Assert.IsType<ObjectResult>(
            await controller.Update(system.Id, new UpdatePlaylistRequest("Renamed"), default));
        Assert.IsType<ObjectResult>(await controller.ClearTracks(system.Id, default));
        Assert.IsType<ObjectResult>(await controller.Delete(system.Id, default));
    }

    [Fact]
    public async Task Playlist_PrivatePlaylistIsInvisibleToOthersButNotAdmins()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        var stranger = Guid.NewGuid();
        sp.DbContext.Users.Add(new User { Id = stranger, Username = "bob", PasswordHash = "x" });
        await sp.DbContext.SaveChangesAsync();

        var created = (PlaylistDetailDto)((CreatedAtRouteResult)(await Playlists(sp).Create(
            new CreatePlaylistRequest("Mine", [lib.Tracks[0].Id]), default)).Result!).Value!;

        var strangerResult = await Playlists(sp, stranger).GetPlaylist(created.Id, default);
        Assert.IsType<ObjectResult>(strangerResult.Result);

        var adminResult = await Playlists(sp, stranger, admin: true).GetPlaylist(created.Id, default);
        Assert.NotNull(adminResult.Value);
    }

    // --- Queue ---

    [Fact]
    public async Task Queue_SaveAndRestore_RoundTripsCurrentTrackAndPosition()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        await sp.DbContext.SaveChangesAsync();

        var controller = Authenticated(new QueueController(sp.Get<PlayQueueService>(), new FakeRatingService()));
        var ids = lib.Tracks.Select(t => t.Id).ToList();

        await controller.SaveQueue(new SaveQueueRequest(ids, ids[1], 42_000), default);
        var restored = (await controller.GetQueue(default)).Value!;

        Assert.Equal(ids, restored.Tracks.Select(t => t.Id));
        Assert.Equal(ids[1], restored.CurrentTrackId);
        Assert.Equal(42_000, restored.PositionMs);
    }

    [Fact]
    public async Task Queue_NoSavedQueue_Is204()
    {
        using var sp = new TestServiceProvider();
        Seed(sp.DbContext);
        await sp.DbContext.SaveChangesAsync();

        var controller = Authenticated(new QueueController(sp.Get<PlayQueueService>(), new FakeRatingService()));

        Assert.IsType<NoContentResult>((await controller.GetQueue(default)).Result);
    }

    [Fact]
    public async Task Queue_UnknownCurrentTrackId_FallsBackToTheHead()
    {
        using var sp = new TestServiceProvider();
        var lib = Seed(sp.DbContext);
        await sp.DbContext.SaveChangesAsync();

        var controller = Authenticated(new QueueController(sp.Get<PlayQueueService>(), new FakeRatingService()));
        var ids = lib.Tracks.Select(t => t.Id).ToList();

        await controller.SaveQueue(new SaveQueueRequest(ids, Guid.NewGuid(), null), default);

        Assert.Equal(ids[0], (await controller.GetQueue(default)).Value!.CurrentTrackId);
    }

    // --- Plays ---

    [Fact]
    public async Task Plays_CountPlayIsTheOnlyThingThatRecordsHistory()
    {
        var plays = new FakePlayHistory();
        var nowPlaying = new FakeNowPlaying();
        var controller = Authenticated(new PlaysController(plays, nowPlaying));
        var trackId = Guid.NewGuid();

        // Progress reports: no history, but now-playing updates.
        await controller.Report(new PlayReportRequest(trackId, 30_000, "playing"), default);
        await controller.Report(new PlayReportRequest(trackId, 31_000, "paused"), default);
        Assert.Empty(plays.Recorded);
        Assert.Equal(2, nowPlaying.Reports.Count);

        // The deliberate signal: exactly one play.
        await controller.Report(new PlayReportRequest(trackId, CountPlay: true), default);
        Assert.Equal(trackId, Assert.Single(plays.Recorded).TrackId);
    }

    [Fact]
    public async Task Plays_UnknownStateNormalizesToPlaying()
    {
        var nowPlaying = new FakeNowPlaying();
        var controller = Authenticated(new PlaysController(new FakePlayHistory(), nowPlaying));

        await controller.Report(new PlayReportRequest(Guid.NewGuid(), State: "warp-speed"), default);

        Assert.Equal(INowPlayingService.StatePlaying, Assert.Single(nowPlaying.Reports).State);
    }

    // --- Me: stars + ratings ---

    [Fact]
    public async Task Me_StarAndUnstar_MapRouteKindsOntoItemTypes()
    {
        var stars = new FakeStars();
        var controller = Authenticated(new MeController(stars, new FakeRatingService()));
        var id = Guid.NewGuid();

        Assert.IsType<NoContentResult>(await controller.Star("tracks", id, default));
        Assert.IsType<NoContentResult>(await controller.Star("albums", id, default));
        Assert.IsType<NoContentResult>(await controller.Unstar("artists", id, default));

        Assert.Equal(["track", "album"], stars.Starred.Select(s => s.ItemType));
        Assert.Equal("artist", Assert.Single(stars.Unstarred).ItemType);
    }

    [Fact]
    public async Task Me_UnknownKind_Is404()
    {
        var controller = Authenticated(new MeController(new FakeStars(), new FakeRatingService()));

        Assert.IsType<NotFoundObjectResult>(await controller.Star("directories", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Me_SetRating_ValidatesRangeAndResolution()
    {
        var ratings = new FakeRatingService();
        var controller = Authenticated(new MeController(new FakeStars(), ratings));
        var id = Guid.NewGuid();

        Assert.IsType<NoContentResult>(await controller.SetRating(id, new SetRatingRequest(4), default));
        Assert.Equal((id, 4), ratings.Calls.Single());

        Assert.IsType<BadRequestObjectResult>(await controller.SetRating(id, new SetRatingRequest(6), default));

        ratings.Resolves = false;
        Assert.IsType<NotFoundObjectResult>(await controller.SetRating(id, new SetRatingRequest(3), default));
    }

    private sealed class FakePlayHistory : IPlayHistoryService
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

    private sealed class FakeNowPlaying : INowPlayingService
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

    private sealed class FakeStars : IStarService
    {
        public List<(string ItemType, Guid ItemId)> Starred { get; } = [];
        public List<(string ItemType, Guid ItemId)> Unstarred { get; } = [];

        public Task<StarredItems> GetStarredAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(new StarredItems([], [], []));

        public Task<IReadOnlyList<Album>> GetStarredAlbumsAsync(Guid userId, int offset, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Album>>([]);

        public Task StarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
        {
            Starred.Add((itemType, itemId));
            return Task.CompletedTask;
        }

        public Task UnstarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
        {
            Unstarred.Add((itemType, itemId));
            return Task.CompletedTask;
        }
    }
}
