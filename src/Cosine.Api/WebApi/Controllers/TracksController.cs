using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

[Route("api/tracks")]
public class TracksController(
    IMediaLibraryService library,
    ISimilarityService similarity,
    ILyricsService lyrics,
    IRatingService ratings) : WebApiController
{
    /// <summary>
    /// A track's lyrics in one call: stored structured sets (synced first), with plain-text
    /// matched by artist/title as the fallback only when no structured set exists. Replaces
    /// the client's getLyricsBySongId-then-getLyrics fallback dance.
    /// </summary>
    [HttpGet("{id:guid}/lyrics")]
    public async Task<ActionResult<TrackLyricsResponse>> GetLyrics(Guid id, CancellationToken ct)
    {
        var track = await library.GetTrackAsync(id, ct);
        if (track is null)
            return NotFoundProblem("Track not found");

        var structured = await lyrics.GetStructuredLyricsAsync(id, ct);
        var plain = structured.Count > 0
            ? null
            : await lyrics.GetPlainLyricsAsync(track.Artist?.Name, track.Title, ct);

        return new TrackLyricsResponse(structured, plain);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TrackDto>> GetTrack(Guid id, CancellationToken ct)
    {
        var track = await library.GetTrackAsync(id, ct);
        if (track is null)
            return NotFoundProblem("Track not found");

        var dto = WebDtoMapper.ToDto(track);
        await ApplyAsync(new UserDataOverlay().Add(dto), ratings, ct);
        return dto;
    }

    [HttpGet("random")]
    public async Task<ActionResult<TrackListResponse>> GetRandom(
        [FromQuery] int size = 20, CancellationToken ct = default)
    {
        var tracks = await library.GetRandomTracksAsync(Math.Clamp(size, 1, 500), ct);
        return await ToResponseAsync(tracks, ct);
    }

    /// <summary>
    /// An artist-radio queue seeded from a track, album, or artist id - whatever the caller
    /// has in hand resolves to its underlying artist, same as Subsonic's getSimilarSongs2.
    /// </summary>
    [HttpGet("/api/similar-tracks")]
    public async Task<ActionResult<TrackListResponse>> GetSimilar(
        [FromQuery] Guid seedId, [FromQuery] int count = 50, CancellationToken ct = default)
    {
        var artistId = await ResolveSeedArtistAsync(seedId, ct);
        if (artistId is null)
            return NotFoundProblem("Seed not found");

        var tracks = await similarity.GetArtistRadioAsync(
            artistId.Value, Math.Clamp(count, 1, 500), ct);
        return await ToResponseAsync(tracks, ct);
    }

    /// <summary>How many seed tracks are read. Only the first few distinct artists are used.</summary>
    private const int MaxAutoPlaySeeds = 10;

    /// <summary>
    /// Ceiling on the exclusion list. An auto-play queue grows without limit, and the whole
    /// point of the list is the recent past - unbounded request bodies are not a thing to
    /// accept from a client.
    /// </summary>
    private const int MaxAutoPlayExclusions = 1000;

    /// <param name="SeedTrackIds">Recently played, most recent first.</param>
    /// <param name="ExcludeTrackIds">What the caller already has queued or has just heard.</param>
    /// <param name="Count">Batch size. Clamped by the service to [1, 500].</param>
    public sealed record AutoPlayRequest(
        IReadOnlyList<Guid> SeedTrackIds,
        IReadOnlyList<Guid>? ExcludeTrackIds = null,
        int Count = 20);

    /// <summary>
    /// The next batch for a queue that should not end - the player's Auto-Play toggle.
    /// POST because the exclusion list is the caller's whole queue, which does not fit in a
    /// URL; nothing is created and nothing here is cacheable.
    /// </summary>
    [HttpPost("/api/autoplay")]
    public async Task<ActionResult<TrackListResponse>> AutoPlay(
        [FromBody] AutoPlayRequest request, CancellationToken ct)
    {
        var seeds = (request.SeedTrackIds ?? []).Take(MaxAutoPlaySeeds).ToList();
        if (seeds.Count == 0)
            return BadRequestProblem("At least one seed track is required");

        // Trimmed from the front: the list arrives most-recent-first, same as the seeds.
        var exclude = (request.ExcludeTrackIds ?? []).Take(MaxAutoPlayExclusions).ToList();

        var tracks = await similarity.GetAutoPlayQueueAsync(seeds, exclude, request.Count, ct);
        return await ToResponseAsync(tracks, ct);
    }

    private async Task<Guid?> ResolveSeedArtistAsync(Guid seedId, CancellationToken ct)
    {
        if (await library.GetArtistAsync(seedId, ct) is { } artist)
            return artist.Id;
        if (await library.GetAlbumAsync(seedId, ct) is { } album)
            return album.ArtistId;
        if (await library.GetTrackAsync(seedId, ct) is { } track)
            return track.ArtistId;
        return null;
    }

    private async Task<TrackListResponse> ToResponseAsync(
        IReadOnlyList<Core.Entities.Track> tracks, CancellationToken ct)
    {
        var dtos = tracks.Select(WebDtoMapper.ToDto).ToList();
        await ApplyAsync(new UserDataOverlay().AddRange(dtos), ratings, ct);
        return new TrackListResponse(dtos);
    }
}
