using System.Security.Claims;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class BrowsingController : BrowsingControllerBase
{
    private readonly IMediaLibraryService _library;
    private readonly ISimilarityService _similarity;
    private readonly IRatingService _ratings;

    public BrowsingController(
        IMediaLibraryService library, ISimilarityService similarity, IRatingService ratings)
    {
        _library = library;
        _similarity = similarity;
        _ratings = ratings;
    }

    public override async Task<ActionResult<GetArtistsResponse>> GetArtists(
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        var artists = await _library.GetArtistsAsync(cancellationToken);

        var grouped = Browsable(artists)
            .GroupBy(a => GetIndexChar(a.SortName))
            .OrderBy(g => g.Key)
            .Select(g => new IndexID3
            {
                Name = g.Key,
                Artist = g.OrderBy(a => a.SortName).Select(MapToArtistID3).ToList()
            })
            .ToList();

        var response = new GetArtistsResponse
        {
            SubsonicResponse = new GetArtistsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Artists = new ArtistsID3
                {
                    Index = grouped,
                    IgnoredArticles = "The A An"
                }
            }
        };

        return Ok(response);
    }

    public override async Task<ActionResult<GetArtistResponse>> GetArtist(
        string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Artist not found");

        var artist = await _library.GetArtistAsync(guid, cancellationToken);
        if (artist is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Artist not found");

        var response = new GetArtistResponse
        {
            SubsonicResponse = new GetArtistSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Artist = new ArtistWithAlbumsID3
                {
                    Id = artist.Id.ToString(),
                    Name = artist.Name,
                    CoverArt = artist.Id.ToString(),
                    AlbumCount = artist.Albums.Count,
                    SortName = artist.SortName,
                    MusicBrainzId = artist.MusicBrainzId,
                    Album = artist.Albums.Select(MapToAlbumID3).ToList()
                }
            }
        };

        // ArtistID3 has no userRating field, so only the albums can carry one here.
        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.Artist.Album), cancellationToken);

        return Ok(response);
    }

    public override async Task<ActionResult<GetAlbumResponse>> GetAlbum(
        string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Album not found");

        var album = await _library.GetAlbumAsync(guid, cancellationToken);
        if (album is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Album not found");

        var response = new GetAlbumResponse
        {
            SubsonicResponse = new GetAlbumSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Album = new AlbumID3WithSongs
                {
                    Id = album.Id.ToString(),
                    Name = album.Name,
                    Artist = album.Artist.Name,
                    ArtistId = album.Artist.Id.ToString(),
                    CoverArt = album.Id.ToString(),
                    SongCount = album.TrackCount,
                    Duration = (int)(album.DurationMs / 1000),
                    Created = album.CreatedAt,
                    Year = album.Year,
                    Song = album.Tracks.Select(MapToChild).ToList()
                }
            }
        };

        var albumNode = response.SubsonicResponse.Album;
        await ApplyRatingsAsync(
            new RatingOverlay().Add(albumNode).AddRange(albumNode.Song), cancellationToken);

        return Ok(response);
    }

    public override async Task<ActionResult<GetSongResponse>> GetSong(
        string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Song not found");

        var track = await _library.GetTrackAsync(guid, cancellationToken);
        if (track is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Song not found");

        var response = new GetSongResponse
        {
            SubsonicResponse = new GetSongSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Song = MapToChild(track)
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().Add(response.SubsonicResponse.Song), cancellationToken);

        return Ok(response);
    }

    public override async Task<ActionResult<GetGenresResponse>> GetGenres(
        CancellationToken cancellationToken = default)
    {
        var genres = await _library.GetGenresAsync(cancellationToken);

        var response = new GetGenresResponse
        {
            SubsonicResponse = new GetGenresSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Genres = new Genres
                {
                    Genre = genres.Select(g => new Genre
                    {
                        Value = g.Name,
                        SongCount = g.SongCount,
                        AlbumCount = g.AlbumCount
                    }).ToList()
                }
            }
        };

        return Ok(response);
    }

    // Skipped: getAlbumInfo, getAlbumInfo2, getArtistInfo, getArtistInfo2,
    // getTopSongs, getSimilarSongs, getSimilarSongs2, getSonicSimilarTracks,
    // findSonicPath, getVideoInfo, getVideos - not in scope

    public override async Task<ActionResult<GetMusicDirectoryResponse>> GetMusicDirectory(
        string id, CancellationToken cancellationToken = default)
    {
        // Try as album ID first, then artist ID
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Not found");

        var album = await _library.GetAlbumAsync(guid, cancellationToken);
        if (album is not null)
        {
            var response = new GetMusicDirectoryResponse
            {
                SubsonicResponse = new GetMusicDirectorySuccessResponse
                {
                    Status = SubsonicSuccessResponseStatus.Ok,
                    Version = "1.16.1",
                    Directory = new Generated.Directory
                    {
                        Id = album.Id.ToString(),
                        Name = album.Name,
                        Parent = album.Artist.Id.ToString(),
                        Child = album.Tracks.Select(MapToChild).ToList()
                    }
                }
            };

            var dir = response.SubsonicResponse.Directory;
            await ApplyRatingsAsync(
                new RatingOverlay().Add(dir, IRatingService.Album).AddRange(dir.Child),
                cancellationToken);

            return Ok(response);
        }

        var artist = await _library.GetArtistAsync(guid, cancellationToken);
        if (artist is not null)
        {
            var response = new GetMusicDirectoryResponse
            {
                SubsonicResponse = new GetMusicDirectorySuccessResponse
                {
                    Status = SubsonicSuccessResponseStatus.Ok,
                    Version = "1.16.1",
                    Directory = new Generated.Directory
                    {
                        Id = artist.Id.ToString(),
                        Name = artist.Name,
                        Child = artist.Albums.Select(a => MapAlbumToChild(a)).ToList()
                    }
                }
            };

            var dir = response.SubsonicResponse.Directory;
            await ApplyRatingsAsync(
                new RatingOverlay().Add(dir, IRatingService.Artist).AddRange(dir.Child),
                cancellationToken);

            return Ok(response);
        }

        throw new SubsonicException(SubsonicErrorCode._70, "Not found");
    }

    public override Task<ActionResult<GetMusicFoldersResponse>> GetMusicFolders(
        CancellationToken cancellationToken = default)
    {
        var response = new GetMusicFoldersResponse
        {
            SubsonicResponse = new GetMusicFoldersSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                MusicFolders = new MusicFolders
                {
                    MusicFolder = new List<MusicFolder>
                    {
                        new() { Id = 1, Name = "Media Library" }
                    }
                }
            }
        };

        return Task.FromResult<ActionResult<GetMusicFoldersResponse>>(Ok(response));
    }

    public override async Task<ActionResult<GetIndexesResponse>> GetIndexes(
        string? musicFolderId = null, int? ifModifiedSince = null, CancellationToken cancellationToken = default)
    {
        var artists = await _library.GetArtistsAsync(cancellationToken);

        var grouped = Browsable(artists)
            .GroupBy(a => GetIndexChar(a.SortName))
            .OrderBy(g => g.Key)
            .Select(g => new Generated.Index
            {
                Name = g.Key,
                Artist = g.OrderBy(a => a.SortName).Select(MapToArtist).ToList()
            })
            .ToList();

        var response = new GetIndexesResponse
        {
            SubsonicResponse = new GetIndexesSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Indexes = new Indexes
                {
                    IgnoredArticles = "The A An",
                    Index = grouped
                }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(grouped.SelectMany(g => g.Artist ?? [])), cancellationToken);

        return Ok(response);
    }

    // POST variants - delegate to GET implementations
    public override Task<ActionResult<GetArtistsResponse>> PostGetArtists(
        Body25? body = null, CancellationToken cancellationToken = default)
        => GetArtists(body?.MusicFolderId, cancellationToken);

    public override Task<ActionResult<GetArtistResponse>> PostGetArtist(
        Body22 body, CancellationToken cancellationToken = default)
        => GetArtist(body.Id, cancellationToken);

    public override Task<ActionResult<GetAlbumResponse>> PostGetAlbum(
        Body17 body, CancellationToken cancellationToken = default)
        => GetAlbum(body.Id, cancellationToken);

    public override Task<ActionResult<GetSongResponse>> PostGetSong(
        Body42 body, CancellationToken cancellationToken = default)
        => GetSong(body.Id, cancellationToken);

    public override Task<ActionResult<GetGenresResponse>> PostGetGenres(
        object? body = null, CancellationToken cancellationToken = default)
        => GetGenres(cancellationToken);

    public override Task<ActionResult<GetMusicDirectoryResponse>> PostGetMusicDirectory(
        Body32 body, CancellationToken cancellationToken = default)
        => GetMusicDirectory(body.Id, cancellationToken);

    public override Task<ActionResult<GetMusicFoldersResponse>> PostGetMusicFolders(
        object? body = null, CancellationToken cancellationToken = default)
        => GetMusicFolders(cancellationToken);

    public override Task<ActionResult<GetIndexesResponse>> PostGetIndexes(
        Body29? body = null, CancellationToken cancellationToken = default)
        => GetIndexes(body?.MusicFolderId, body?.IfModifiedSince, cancellationToken);

    // Skipped POST variants for out-of-scope endpoints
    public override Task<ActionResult<GetAlbumInfoResponse>> GetAlbumInfo(string id, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<GetAlbumInfoResponse>> PostGetAlbumInfo(Body18 body, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<GetAlbumInfoResponse>> GetAlbumInfo2(string id, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<GetAlbumInfoResponse>> PostGetAlbumInfo2(Body19 body, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    // Artist Radio endpoints - backed by ISimilarityService's local scoring. These
    // map onto the Subsonic contract so the feature works in the web UI and every
    // Subsonic client at once. getAlbumInfo, findSonicPath and the video endpoints
    // remain out of scope below.

    public override async Task<ActionResult<GetArtistInfoResponse>> GetArtistInfo(string id, int? count = 20, bool? includeNotPresent = false, CancellationToken cancellationToken = default)
    {
        var (artist, _) = await ResolveSeedArtistAsync(id, cancellationToken);
        if (artist is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Artist not found");

        var similar = await _similarity.GetSimilarArtistsAsync(artist.Id, count ?? 20, cancellationToken);

        return Ok(new GetArtistInfoResponse
        {
            SubsonicResponse = new GetArtistInfoSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                ArtistInfo = new ArtistInfo
                {
                    Biography = artist.Biography,
                    MusicBrainzId = artist.MusicBrainzId,
                    SimilarArtist = similar.Select(s => MapToArtist(s.Artist)).ToList()
                }
            }
        });
    }

    public override Task<ActionResult<GetArtistInfoResponse>> PostGetArtistInfo(Body23 body, CancellationToken cancellationToken = default)
        => GetArtistInfo(body.Id, body.Count, body.IncludeNotPresent, cancellationToken);

    public override async Task<ActionResult<GetArtistInfo2Response>> GetArtistInfo2(string id, int? count = 20, bool? includeNotPresent = false, CancellationToken cancellationToken = default)
    {
        var (artist, _) = await ResolveSeedArtistAsync(id, cancellationToken);
        if (artist is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Artist not found");

        var similar = await _similarity.GetSimilarArtistsAsync(artist.Id, count ?? 20, cancellationToken);

        return Ok(new GetArtistInfo2Response
        {
            SubsonicResponse = new GetArtistInfo2SuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                ArtistInfo2 = new ArtistInfo2
                {
                    Biography = artist.Biography,
                    MusicBrainzId = artist.MusicBrainzId,
                    SimilarArtist = similar.Select(s => MapToArtistID3(s.Artist)).ToList()
                }
            }
        });
    }

    public override Task<ActionResult<GetArtistInfo2Response>> PostGetArtistInfo2(Body24 body, CancellationToken cancellationToken = default)
        => GetArtistInfo2(body.Id, body.Count, body.IncludeNotPresent, cancellationToken);

    public override async Task<ActionResult<GetTopSongsResponse>> GetTopSongs(string id, int? count = 50, CancellationToken cancellationToken = default)
    {
        var artistId = await ResolveTopSongsArtistAsync(id, cancellationToken);
        if (artistId is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Artist not found");

        var tracks = await _similarity.GetTopSongsAsync(artistId.Value, count ?? 50, cancellationToken);

        var response = new GetTopSongsResponse
        {
            SubsonicResponse = new GetTopSongsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                TopSongs = new TopSongs { Song = tracks.Select(MapToChild).ToList() }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.TopSongs.Song), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetTopSongsResponse>> PostGetTopSongs(Body44 body, CancellationToken cancellationToken = default)
        => GetTopSongs(body.Id, body.Count, cancellationToken);

    public override async Task<ActionResult<GetSimilarSongsResponse>> GetSimilarSongs(string id, int? count = 50, CancellationToken cancellationToken = default)
    {
        var tracks = await GetRadioTracksAsync(id, count ?? 50, cancellationToken);

        var response = new GetSimilarSongsResponse
        {
            SubsonicResponse = new GetSimilarSongsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                SimilarSongs = new SimilarSongs { Song = tracks.Select(MapToChild).ToList() }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.SimilarSongs.Song), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetSimilarSongsResponse>> PostGetSimilarSongs(Body39 body, CancellationToken cancellationToken = default)
        => GetSimilarSongs(body.Id, body.Count, cancellationToken);

    public override async Task<ActionResult<GetSimilarSongs2Response>> GetSimilarSongs2(string id, int? count = 50, CancellationToken cancellationToken = default)
    {
        var tracks = await GetRadioTracksAsync(id, count ?? 50, cancellationToken);

        var response = new GetSimilarSongs2Response
        {
            SubsonicResponse = new GetSimilarSongs2SuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                SimilarSongs2 = new SimilarSongs2 { Song = tracks.Select(MapToChild).ToList() }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.SimilarSongs2.Song), cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetSimilarSongs2Response>> PostGetSimilarSongs2(Body40 body, CancellationToken cancellationToken = default)
        => GetSimilarSongs2(body.Id, body.Count, cancellationToken);

    public override async Task<ActionResult<GetSonicSimilarTracksResponse>> GetSonicSimilarTracks(string id, int? count = 10, CancellationToken cancellationToken = default)
    {
        // No true acoustic similarity yet - alias to the radio result and expose a
        // rank-based decay in [0, 1] so clients still get an ordered, usable list.
        var tracks = await GetRadioTracksAsync(id, count ?? 10, cancellationToken);
        var n = tracks.Count;

        var response = new GetSonicSimilarTracksResponse
        {
            SubsonicResponse = new GetSonicSimilarTracksSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                SonicMatch = tracks
                    .Select((t, i) => new SonicMatch
                    {
                        Entry = MapToChild(t),
                        Similarity = n > 1 ? Math.Round(1.0 - (double)i / n, 4) : 1.0
                    })
                    .ToList()
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(response.SubsonicResponse.SonicMatch.Select(m => m.Entry)),
            cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetSonicSimilarTracksResponse>> PostGetSonicSimilarTracks(Body41 body, CancellationToken cancellationToken = default)
        => GetSonicSimilarTracks(body.Id, body.Count, cancellationToken);

    // Resolves a Subsonic id (artist, album, or song) to a radio queue.
    private async Task<IReadOnlyList<Core.Entities.Track>> GetRadioTracksAsync(string id, int count, CancellationToken ct)
    {
        var (_, artistId) = await ResolveSeedArtistAsync(id, ct);
        if (artistId is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Not found");
        return await _similarity.GetArtistRadioAsync(artistId.Value, count, ct);
    }

    // A Subsonic id may point at an artist, album, or song; all three seed a radio
    // from the underlying artist.
    private async Task<(Core.Entities.Artist? Artist, Guid? ArtistId)> ResolveSeedArtistAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid))
            return (null, null);

        var artist = await _library.GetArtistAsync(guid, ct);
        if (artist is not null)
            return (artist, artist.Id);

        var album = await _library.GetAlbumAsync(guid, ct);
        if (album is not null)
            return (await _library.GetArtistAsync(album.ArtistId, ct), album.ArtistId);

        var track = await _library.GetTrackAsync(guid, ct);
        if (track is not null)
            return (await _library.GetArtistAsync(track.ArtistId, ct), track.ArtistId);

        return (null, null);
    }

    // Subsonic getTopSongs is keyed on the artist *name* (the `artist` param), not
    // an id. Fall back to treating `id` as an artist id for id-based callers.
    private async Task<Guid?> ResolveTopSongsArtistAsync(string? id, CancellationToken ct)
    {
        var artistName = Request.Query["artist"].ToString();
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            var artists = await _library.GetArtistsAsync(ct);
            return artists
                .FirstOrDefault(a => string.Equals(a.Name, artistName, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }

        return Guid.TryParse(id, out var guid) ? guid : null;
    }

    public override Task<ActionResult<FindSonicPathResponse>> FindSonicPath(string startSongId, string endSongId, int? count = 25, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<FindSonicPathResponse>> PostFindSonicPath(Body16 body, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<GetVideoInfoResponse>> GetVideoInfo(string id, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<GetVideoInfoResponse>> PostGetVideoInfo(Body46 body, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<GetVideosResponse>> GetVideos(CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    public override Task<ActionResult<GetVideosResponse>> PostGetVideos(object? body = null, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "Not implemented");

    /// <summary>
    /// Applies the per-user rating overlay to a response that has already been mapped. It runs
    /// after the library object comes back precisely because that object may have come from the
    /// shared browse cache - see <see cref="RatingOverlay"/>.
    /// </summary>
    private Task ApplyRatingsAsync(RatingOverlay overlay, CancellationToken ct)
        => overlay.ApplyAsync(_ratings, GetCurrentUserId(), ct);

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.Parse(sub!);
    }

    // Mapping helpers
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

    private static Child MapToChild(Core.Entities.Track t) => SongMapping.ToChild(t);

    private static Child MapAlbumToChild(Core.Entities.Album a) => new()
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

    private static Artist MapToArtist(Core.Entities.Artist a) => new()
    {
        Id = a.Id.ToString(),
        Name = a.Name
    };

    /// <summary>
    /// The artist rows worth putting in an A–Z index. Featured/guest credits create an
    /// <see cref="Core.Entities.Artist"/> row that owns no album of its own - see
    /// <c>TrackArtist</c> - and those swamp the index with names that have no image, no
    /// discography, and nothing to browse into. Starred artists stay regardless, so
    /// favouriting a guest credit doesn't make it vanish from the list it was starred in.
    /// Both are still reachable by id (getArtist), by name (getTopSongs) and via search.
    /// </summary>
    private static IEnumerable<Core.Entities.Artist> Browsable(IEnumerable<Core.Entities.Artist> artists)
        => artists.Where(a => a.Albums.Count > 0 || a.StarCount > 0);

    private static string GetIndexChar(string sortName)
    {
        if (string.IsNullOrEmpty(sortName)) return "#";
        var c = char.ToUpperInvariant(sortName[0]);
        return c is >= 'A' and <= 'Z' ? c.ToString() : "#";
    }
}
