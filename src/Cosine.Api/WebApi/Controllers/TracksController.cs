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

    /// <summary>How many recent plays are read. The station weighting uses fewer still.</summary>
    private const int MaxAutoPlaySeeds = 10;

    /// <summary>
    /// Ceiling on the exclusion list. An auto-play queue grows without limit, and the whole
    /// point of the list is the recent past - unbounded request bodies are not a thing to
    /// accept from a client.
    /// </summary>
    private const int MaxAutoPlayExclusions = 1000;

    /// <summary>Ceiling on the anchor and vote lists. The anchor is sampled further server-side.</summary>
    private const int MaxStationTracks = 500;

    /// <param name="SeedTrackIds">Recently played, most recent first.</param>
    /// <param name="ExcludeTrackIds">What the caller already has queued or has just heard.</param>
    /// <param name="Count">Batch size. Clamped by the service to [1, 500].</param>
    /// <param name="AnchorTrackIds">The tracks the listener chose for this station.</param>
    /// <param name="LikedTrackIds">Tracks marked "+".</param>
    /// <param name="DislikedTrackIds">Tracks marked "−".</param>
    /// <param name="AutoPlayedTrackIds">Tracks auto-play itself queued; they count less as seeds.</param>
    public sealed record AutoPlayRequest(
        IReadOnlyList<Guid>? SeedTrackIds,
        IReadOnlyList<Guid>? ExcludeTrackIds = null,
        int Count = 20,
        IReadOnlyList<Guid>? AnchorTrackIds = null,
        IReadOnlyList<Guid>? LikedTrackIds = null,
        IReadOnlyList<Guid>? DislikedTrackIds = null,
        IReadOnlyList<Guid>? AutoPlayedTrackIds = null);

    /// <summary>
    /// The next batch for a queue that should not end - the player's Auto-Play toggle.
    /// POST because the exclusion list is the caller's whole queue, which does not fit in a
    /// URL; nothing is created and nothing here is cacheable.
    /// </summary>
    [HttpPost("/api/autoplay")]
    public async Task<ActionResult<TrackListResponse>> AutoPlay(
        [FromBody] AutoPlayRequest request, CancellationToken ct)
    {
        var station = new AutoPlayStation(
            (request.SeedTrackIds ?? []).Take(MaxAutoPlaySeeds).ToList(),
            (request.AnchorTrackIds ?? []).Take(MaxStationTracks).ToList(),
            (request.LikedTrackIds ?? []).Take(MaxStationTracks).ToList(),
            (request.DislikedTrackIds ?? []).Take(MaxStationTracks).ToList(),
            (request.AutoPlayedTrackIds ?? []).Take(MaxAutoPlayExclusions).ToList());

        if (station.Recent.Count == 0 && station.AnchorOrEmpty.Count == 0 && station.LikedOrEmpty.Count == 0)
            return BadRequestProblem("At least one seed, anchor or liked track is required");

        // Trimmed from the front: the list arrives most-recent-first, same as the seeds.
        var exclude = (request.ExcludeTrackIds ?? []).Take(MaxAutoPlayExclusions).ToList();

        var tracks = await similarity.GetAutoPlayQueueAsync(station, exclude, request.Count, ct);
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
