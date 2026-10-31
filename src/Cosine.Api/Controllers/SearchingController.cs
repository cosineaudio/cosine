using System.Security.Claims;
using Cosine.Api.Generated;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class SearchingController : SearchingControllerBase
{
    private readonly ISearchService _search;
    private readonly IRatingService _ratings;

    public SearchingController(ISearchService search, IRatingService ratings)
    {
        _search = search;
        _ratings = ratings;
    }

    // OpenSubsonic: an empty query means "return everything" - clients use this for
    // full library sync. Symfonium sends literal double quotes ("") for it. MVC binds
    // an empty query= value as null, so query can be null despite its type.
    private static bool IsBrowseAll(string? query)
    {
        var q = query?.Trim() ?? string.Empty;
        return q.Length == 0 || q == "\"\"" || q == "*";
    }

    public override async Task<ActionResult<Search3Response>> Search3(
        string query, int? artistCount = 20, int? artistOffset = 0,
        int? albumCount = 20, int? albumOffset = 0,
        int? songCount = 20, int? songOffset = 0,
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        var browseAll = IsBrowseAll(query);
        var results = browseAll
            ? await _search.BrowseAllAsync(
                artistCount ?? 20, artistOffset ?? 0,
                albumCount ?? 20, albumOffset ?? 0,
                songCount ?? 20, songOffset ?? 0,
                cancellationToken)
            : await _search.SearchAsync(
                query,
                (artistCount ?? 20) + (artistOffset ?? 0),
                (albumCount ?? 20) + (albumOffset ?? 0),
                (songCount ?? 20) + (songOffset ?? 0),
                cancellationToken);

        // BrowseAllAsync pages in the database; the search path pages here.
        var artists = (browseAll ? results.Artists : results.Artists.Skip(artistOffset ?? 0).Take(artistCount ?? 20))
            .Select(MapToArtistID3).ToList();

        var albums = (browseAll ? results.Albums : results.Albums.Skip(albumOffset ?? 0).Take(albumCount ?? 20))
            .Select(MapToAlbumID3).ToList();

        var songs = (browseAll ? results.Tracks : results.Tracks.Skip(songOffset ?? 0).Take(songCount ?? 20))
            .Select(MapToChild).ToList();

        var response = new Search3Response
        {
            SubsonicResponse = new Search3SuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                SearchResult3 = new SearchResult3
                {
                    Artist = artists,
                    Album = albums,
                    Song = songs
                }
            }
        };

        // ArtistID3 carries no userRating field, so search3's artists cannot show one.
        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(albums).AddRange(songs), cancellationToken);

        return Ok(response);
    }

    public override async Task<ActionResult<Search2Response>> Search2(
        string query, int? artistCount = 20, int? artistOffset = 0,
        int? albumCount = 20, int? albumOffset = 0,
        int? songCount = 20, int? songOffset = 0,
        string? musicFolderId = null, CancellationToken cancellationToken = default)
    {
        var browseAll = IsBrowseAll(query);
        var results = browseAll
            ? await _search.BrowseAllAsync(
                artistCount ?? 20, artistOffset ?? 0,
                albumCount ?? 20, albumOffset ?? 0,
                songCount ?? 20, songOffset ?? 0,
                cancellationToken)
            : await _search.SearchAsync(
                query,
                (artistCount ?? 20) + (artistOffset ?? 0),
                (albumCount ?? 20) + (albumOffset ?? 0),
                (songCount ?? 20) + (songOffset ?? 0),
                cancellationToken);

        // Search2 uses the simpler Artist type
        var artists = (browseAll ? results.Artists : results.Artists.Skip(artistOffset ?? 0).Take(artistCount ?? 20))
            .Select(a => new Artist { Id = a.Id.ToString(), Name = a.Name }).ToList();

        var albums = (browseAll ? results.Albums : results.Albums.Skip(albumOffset ?? 0).Take(albumCount ?? 20))
            .Select(MapToChildAlbum).ToList();

        var songs = (browseAll ? results.Tracks : results.Tracks.Skip(songOffset ?? 0).Take(songCount ?? 20))
            .Select(MapToChild).ToList();

        var response = new Search2Response
        {
            SubsonicResponse = new Search2SuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                SearchResult2 = new SearchResult2
                {
                    Artist = artists,
                    Album = albums,
                    Song = songs
                }
            }
        };

        await ApplyRatingsAsync(
            new RatingOverlay().AddRange(artists).AddRange(albums).AddRange(songs), cancellationToken);

        return Ok(response);
    }

    [Obsolete]
    public override async Task<ActionResult<SearchResponse>> Search(
        string? artist = null, string? album = null, string? title = null,
        bool? any = null, int? count = 20, int? offset = 0,
        int? newerThan = null, CancellationToken cancellationToken = default)
    {
        var query = artist ?? album ?? title ?? string.Empty;
        var results = await _search.SearchAsync(query, 0, 0, (count ?? 20) + (offset ?? 0), cancellationToken);

        var songs = results.Tracks
            .Skip(offset ?? 0).Take(count ?? 20)
            .Select(MapToChild).ToList();

        // The spec models searchResult as an empty element, so the generated SearchResult has
        // no typed slot for the hits. They go through the extension data, which serializes to
        // the same wire shape the v1.0.0 clients expect: <searchResult><match .../></searchResult>.
        var searchResult = new SearchResult();
        searchResult.AdditionalProperties["match"] = songs;

        return Ok(new SearchResponse
        {
            SubsonicResponse = new SearchSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                SearchResult = searchResult
            }
        });
    }

    // POST variants
    public override Task<ActionResult<Search3Response>> PostSearch3(
        Body55 body, CancellationToken cancellationToken = default)
        => Search3(body.Query, body.ArtistCount, body.ArtistOffset,
            body.AlbumCount, body.AlbumOffset,
            body.SongCount, body.SongOffset,
            body.MusicFolderId, cancellationToken);

    public override Task<ActionResult<Search2Response>> PostSearch2(
        Body54 body, CancellationToken cancellationToken = default)
        => Search2(body.Query, body.ArtistCount, body.ArtistOffset,
            body.AlbumCount, body.AlbumOffset,
            body.SongCount, body.SongOffset,
            body.MusicFolderId, cancellationToken);

    [Obsolete]
    public override Task<ActionResult<SearchResponse>> PostSearch(
        Body53 body, CancellationToken cancellationToken = default)
        => Search(body.Artist, body.Album, body.Title,
            body.Any, body.Count, body.Offset,
            body.NewerThan, cancellationToken);

    // Mapping helpers
    private Task ApplyRatingsAsync(RatingOverlay overlay, CancellationToken ct)
        => overlay.ApplyAsync(_ratings, GetCurrentUserId(), ct);

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.Parse(sub!);
    }

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

    private static Child MapToChildAlbum(Core.Entities.Album a) => new()
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
}
