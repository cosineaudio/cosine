using System.Security.Claims;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class ListsController : ListsControllerBase
{
    private readonly IMediaLibraryService _library;
    private readonly IPlayHistoryService _playHistory;
    private readonly IStarService _starService;
    private readonly INowPlayingService _nowPlaying;
    private readonly IRatingService _ratings;

    public ListsController(
        IMediaLibraryService library,
        IPlayHistoryService playHistory,
        IStarService starService,
        INowPlayingService nowPlaying,
        IRatingService ratings)
    {
        _library = library;
        _playHistory = playHistory;
        _starService = starService;
        _nowPlaying = nowPlaying;
        _ratings = ratings;
    }

    public override async Task<ActionResult<GetAlbumListResponse>> GetAlbumList(
        AlbumListType type, int? size = 10, int? offset = 0,
        int? fromYear = null, int? toYear = null, string? genre = null,
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(size ?? 10, 1, 500);
        var albums = await GetAlbumsByType(type, limit, offset ?? 0, fromYear, toYear, genre, cancellationToken);

        var response = new GetAlbumListResponse
        {
            SubsonicResponse = new GetAlbumListSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                AlbumList = new Generated.AlbumList
                {
                    Album = albums.Select(MapToChild).ToList()
                }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.AlbumList.Album), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetAlbumListResponse>> PostGetAlbumList(
        Body20 body, CancellationToken cancellationToken = default)
    {
        return GetAlbumList(body.Type, body.Size, body.Offset,
            body.FromYear, body.ToYear, body.Genre, body.MusicFolderId, cancellationToken);
    }

    public override async Task<ActionResult<GetAlbumList2Response>> GetAlbumList2(
        AlbumListType type, int? size = 10, int? offset = 0,
        int? fromYear = null, int? toYear = null, string? genre = null,
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(size ?? 10, 1, 500);
        var albums = await GetAlbumsByType(type, limit, offset ?? 0, fromYear, toYear, genre, cancellationToken);

        var response = new GetAlbumList2Response
        {
            SubsonicResponse = new GetAlbumList2SuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                AlbumList2 = new Generated.AlbumList2
                {
                    Album = albums.Select(MapToAlbumID3).ToList()
                }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.AlbumList2.Album), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetAlbumList2Response>> PostGetAlbumList2(
        Body21 body, CancellationToken cancellationToken = default)
    {
        return GetAlbumList2(body.Type, body.Size, body.Offset,
            body.FromYear, body.ToYear, body.Genre, body.MusicFolderId, cancellationToken);
    }

    public override async Task<ActionResult<GetNowPlayingResponse>> GetNowPlaying(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = GetCurrentUserId();
        var entries = new List<NowPlayingEntry>();

        foreach (var report in _nowPlaying.GetCurrent())
        {
            // Playback is private to each user: this returns only the caller's own sessions,
            // never what other accounts on the instance are listening to.
            if (report.UserId != userId)
                continue;

            // A report can outlive its track (removed by a scan between the two); skip it
            // rather than emitting an entry with no metadata.
            var track = await _library.GetTrackAsync(report.TrackId, cancellationToken);
            if (track is null)
                continue;

            entries.Add(MapToNowPlayingEntry(track, report, now));
        }

        var response = new GetNowPlayingResponse
        {
            SubsonicResponse = new GetNowPlayingSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                NowPlaying = new Generated.NowPlaying
                {
                    Entry = entries
                }
            }
        };

        // NowPlayingEntry extends Child, so it carries userRating like any other song node.
        await ApplyRatingsAsync(new RatingOverlay().AddRange(entries), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetNowPlayingResponse>> PostGetNowPlaying(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetNowPlaying(cancellationToken);
    }

    public override async Task<ActionResult<GetRandomSongsResponse>> GetRandomSongs(
        int? size = 10, string? genre = null, int? fromYear = null,
        int? toYear = null, string? musicFolderId = null,
        CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(size ?? 10, 1, 500);
        // GetRandom returns albums without tracks; query tracks directly for random songs
        var tracks = await _library.GetRandomTracksAsync(limit, cancellationToken);

        var response = new GetRandomSongsResponse
        {
            SubsonicResponse = new GetRandomSongsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                RandomSongs = new Generated.Songs
                {
                    Song = tracks.Select(MapToChild).ToList()
                }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.RandomSongs.Song), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetRandomSongsResponse>> PostGetRandomSongs(
        Body38? body = null, CancellationToken cancellationToken = default)
    {
        return GetRandomSongs(body?.Size, body?.Genre, body?.FromYear,
            body?.ToYear, body?.MusicFolderId, cancellationToken);
    }

    public override async Task<ActionResult<GetSongsByGenreResponse>> GetSongsByGenre(
        string genre, int? count = 10, int? offset = 0,
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(count ?? 10, 1, 500);
        var tracks = await _library.GetTracksByGenreAsync(genre, offset ?? 0, limit, cancellationToken);

        var response = new GetSongsByGenreResponse
        {
            SubsonicResponse = new GetSongsByGenreSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                SongsByGenre = new Generated.Songs
                {
                    Song = tracks.Select(MapToChild).ToList()
                }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.SongsByGenre.Song), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetSongsByGenreResponse>> PostGetSongsByGenre(
        Body43 body, CancellationToken cancellationToken = default)
    {
        return GetSongsByGenre(body.Genre, body.Count, body.Offset,
            body.MusicFolderId, cancellationToken);
    }

    public override async Task<ActionResult<GetStarredResponse>> GetStarred(
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var starred = await _starService.GetStarredAsync(userId, cancellationToken);

        var response = new GetStarredResponse
        {
            SubsonicResponse = new GetStarredSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Starred = new Generated.Starred
                {
                    Artist = starred.Artists.Select(MapToArtist).ToList(),
                    Album = starred.Albums.Select(MapToChild).ToList(),
                    Song = starred.Tracks.Select(MapToChild).ToList()
                }
            }
        };

        var node = response.SubsonicResponse.Starred;
        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(node.Artist).AddRange(node.Album).AddRange(node.Song),
            cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetStarredResponse>> PostGetStarred(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetStarred(null, cancellationToken);
    }

    public override async Task<ActionResult<GetStarred2Response>> GetStarred2(
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var starred = await _starService.GetStarredAsync(userId, cancellationToken);

        var response = new GetStarred2Response
        {
            SubsonicResponse = new GetStarred2SuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Starred2 = new Generated.Starred2
                {
                    Artist = starred.Artists.Select(MapToArtistID3).ToList(),
                    Album = starred.Albums.Select(MapToAlbumID3).ToList(),
                    Song = starred.Tracks.Select(MapToChild).ToList()
                }
            }
        };

        // ArtistID3 carries no userRating field, so starred2's artists cannot show one.
        var node = response.SubsonicResponse.Starred2;
        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(node.Album).AddRange(node.Song), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetStarred2Response>> PostGetStarred2(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetStarred2(null, cancellationToken);
    }

    // Album list type resolver
    private async Task<IReadOnlyList<Core.Entities.Album>> GetAlbumsByType(
        AlbumListType type, int limit, int offset,
        int? fromYear, int? toYear, string? genre,
        CancellationToken ct)
    {
        return type switch
        {
            AlbumListType.Random => await _library.GetRandomAsync(limit, ct),
            AlbumListType.Newest => await _library.GetRecentlyAddedAsync(limit, offset, ct),
            AlbumListType.Highest => await _library.GetMostStarredAlbumsAsync(offset, limit, ct),
            AlbumListType.Frequent => await GetFrequentAlbumsAsync(limit, offset, ct),
            AlbumListType.Recent => await GetRecentlyPlayedAlbumsAsync(limit, offset, ct),
            AlbumListType.AlphabeticalByName => await _library.GetAlbumsAlphabeticalAsync(byArtist: false, offset, limit, ct),
            AlbumListType.AlphabeticalByArtist => await _library.GetAlbumsAlphabeticalAsync(byArtist: true, offset, limit, ct),
            AlbumListType.Starred => await GetStarredAlbumsAsync(limit, offset, ct),
            AlbumListType.ByYear => await _library.GetAlbumsByYearAsync(fromYear ?? 0, toYear ?? DateTime.UtcNow.Year, offset, limit, ct),
            AlbumListType.ByGenre => string.IsNullOrEmpty(genre)
                ? throw new SubsonicException(SubsonicErrorCode._10, "Required parameter 'genre' is missing")
                : await _library.GetAlbumsByGenreAsync(genre, offset, limit, ct),
            _ => throw new SubsonicException(SubsonicErrorCode._0, "Unknown list type")
        };
    }

    private async Task<IReadOnlyList<Core.Entities.Album>> GetStarredAlbumsAsync(int limit, int offset, CancellationToken ct)
        => await _starService.GetStarredAlbumsAsync(GetCurrentUserId(), offset, limit, ct);

    private Task ApplyRatingsAsync(RatingOverlay overlay, CancellationToken ct)
        => overlay.ApplyAsync(_ratings, GetCurrentUserId(), ct);

    // Mapping helpers
    private static Child MapToChild(Core.Entities.Album a) => new()
    {
        Id = a.Id.ToString(),
        Parent = a.ArtistId.ToString(),
        IsDir = true,
        Title = a.Name,
        Album = a.Name,
        Artist = a.Artist.Name,
        Year = a.Year,
        CoverArt = a.Id.ToString()
    };

    private static Child MapToChild(Core.Entities.Track t) => SongMapping.ToChild(t);

    private static NowPlayingEntry MapToNowPlayingEntry(
        Core.Entities.Track t, Core.Interfaces.NowPlayingReport report, DateTimeOffset now)
    {
        var entry = SongMapping.Fill(new NowPlayingEntry(), t);
        entry.Username = report.Username;
        entry.MinutesAgo = (int)Math.Max(0, (now - report.ReportedAt).TotalMinutes);
        entry.PositionMs = report.PositionMs;
        entry.State = report.State switch
        {
            INowPlayingService.StatePaused => NowPlayingEntryState.Paused,
            _ => NowPlayingEntryState.Playing
        };
        return entry;
    }

    private static Artist MapToArtist(Core.Entities.Artist a) => new()
    {
        Id = a.Id.ToString(),
        Name = a.Name
    };

    private static ArtistID3 MapToArtistID3(Core.Entities.Artist a) => new()
    {
        Id = a.Id.ToString(),
        Name = a.Name,
        CoverArt = a.Id.ToString(),
        AlbumCount = a.Albums.Count,
        SortName = a.SortName,
        MusicBrainzId = a.MusicBrainzId
    };

    private static AlbumID3 MapToAlbumID3(Core.Entities.Album a) => new()
    {
        Id = a.Id.ToString(),
        Name = a.Name,
        Artist = a.Artist.Name,
        ArtistId = a.Artist.Id.ToString(),
        CoverArt = a.Id.ToString(),
        SongCount = a.TrackCount,
        Duration = (int)(a.DurationMs / 1000),
        Created = a.CreatedAt,
        Year = a.Year
    };

    private async Task<IReadOnlyList<Core.Entities.Album>> GetFrequentAlbumsAsync(int limit, int offset, CancellationToken ct)
    {
        var mostPlayed = await _playHistory.GetMostPlayedAsync(GetCurrentUserId(), (offset + limit) * 5, ct);
        return mostPlayed
            .GroupBy(x => x.Track.AlbumId)
            .Select(g => new { g.First().Track.Album, Plays = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.Plays)
            .Skip(offset)
            .Take(limit)
            .Select(x => x.Album)
            .ToList();
    }

    private async Task<IReadOnlyList<Core.Entities.Album>> GetRecentlyPlayedAlbumsAsync(int limit, int offset, CancellationToken ct)
    {
        var recentTracks = await _playHistory.GetRecentPlaysAsync(GetCurrentUserId(), (offset + limit) * 5, ct);
        return recentTracks
            .GroupBy(t => t.AlbumId)
            .Select(g => g.First().Album)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.Parse(sub!);
    }
}
