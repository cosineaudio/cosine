using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>How <c>GET /api/albums</c> orders its page. Replaces Subsonic's getAlbumList2 types.</summary>
public enum AlbumSort
{
    /// <summary>Most recently added to the library first.</summary>
    Newest,
    /// <summary>The caller's most recently played first.</summary>
    Recent,
    /// <summary>The caller's most played first.</summary>
    Frequent,
    Random,
    /// <summary>Alphabetical by album name.</summary>
    Name,
    /// <summary>Alphabetical by artist name.</summary>
    Artist,
    /// <summary>The caller's starred albums, newest star first.</summary>
    Starred,
    /// <summary>Most starred across all users first (Subsonic's "highest").</summary>
    Popular,
}

[Route("api/albums")]
public class AlbumsController(
    IMediaLibraryService library,
    IPlayHistoryService playHistory,
    IStarService stars,
    IRatingService ratings) : WebApiController
{
    /// <summary>
    /// One page of albums. <paramref name="genre"/> and the year range are filters that take
    /// precedence over <paramref name="sort"/> - they page through the filtered set in the
    /// library's own order, mirroring what byGenre/byYear did on the Subsonic surface.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<AlbumListResponse>> GetAlbums(
        [FromQuery] AlbumSort sort = AlbumSort.Newest,
        [FromQuery] string? genre = null,
        [FromQuery] int? fromYear = null,
        [FromQuery] int? toYear = null,
        [FromQuery] int size = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(size, 1, 500);
        offset = Math.Max(0, offset);

        IReadOnlyList<Core.Entities.Album> albums;
        if (!string.IsNullOrEmpty(genre))
            albums = await library.GetAlbumsByGenreAsync(genre, offset, limit, ct);
        else if (fromYear.HasValue || toYear.HasValue)
            albums = await library.GetAlbumsByYearAsync(
                fromYear ?? 0, toYear ?? DateTime.UtcNow.Year, offset, limit, ct);
        else
            albums = sort switch
            {
                AlbumSort.Newest => await library.GetRecentlyAddedAsync(limit, offset, ct),
                AlbumSort.Recent => await RecentlyPlayedAsync(limit, offset, ct),
                AlbumSort.Frequent => await FrequentAsync(limit, offset, ct),
                AlbumSort.Random => await library.GetRandomAsync(limit, ct),
                AlbumSort.Name => await library.GetAlbumsAlphabeticalAsync(byArtist: false, offset, limit, ct),
                AlbumSort.Artist => await library.GetAlbumsAlphabeticalAsync(byArtist: true, offset, limit, ct),
                AlbumSort.Starred => await stars.GetStarredAlbumsAsync(CurrentUserId, offset, limit, ct),
                AlbumSort.Popular => await library.GetMostStarredAlbumsAsync(offset, limit, ct),
                _ => await library.GetRecentlyAddedAsync(limit, offset, ct),
            };

        var dtos = albums.Select(WebDtoMapper.ToSummaryDto).ToList();
        await ApplyAsync(new UserDataOverlay().AddRange(dtos), ratings, ct);
        return new AlbumListResponse(dtos);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AlbumDetailDto>> GetAlbum(Guid id, CancellationToken ct)
    {
        var album = await library.GetAlbumAsync(id, ct);
        if (album is null)
            return NotFoundProblem("Album not found");

        var dto = WebDtoMapper.ToDetailDto(album);
        await ApplyAsync(new UserDataOverlay().Add(dto).AddRange(dto.Tracks), ratings, ct);
        return dto;
    }

    // "Recent" and "frequent" are the caller's own listening history rolled up to albums.
    // Same over-fetch heuristic as the Subsonic surface: history rows are per-track, so a
    // page of albums needs several pages of plays.
    private async Task<IReadOnlyList<Core.Entities.Album>> RecentlyPlayedAsync(
        int limit, int offset, CancellationToken ct)
    {
        var recent = await playHistory.GetRecentPlaysAsync(CurrentUserId, (offset + limit) * 5, ct);
        return recent
            .GroupBy(t => t.AlbumId)
            .Select(g => g.First().Album)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    private async Task<IReadOnlyList<Core.Entities.Album>> FrequentAsync(
        int limit, int offset, CancellationToken ct)
    {
        var mostPlayed = await playHistory.GetMostPlayedAsync(CurrentUserId, (offset + limit) * 5, ct);
        return mostPlayed
            .GroupBy(x => x.Track.AlbumId)
            .Select(g => new { g.First().Track.Album, Plays = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.Plays)
            .Skip(offset)
            .Take(limit)
            .Select(x => x.Album)
            .ToList();
    }
}
