using Cosine.Core.Dtos;

namespace Cosine.Api.WebApi.Dtos;

/// <summary>
/// One play report from the player. Replaces the client's use of Subsonic <c>scrobble</c>:
/// <see cref="CountPlay"/> true is the deliberate "this counts as a listen" signal (the only
/// thing that increments play history); false is a now-playing/progress notification.
/// </summary>
public sealed record PlayReportRequest(
    Guid TrackId,
    int? PositionMs = null,
    string? State = null,
    bool CountPlay = false);

/// <summary>0 clears the rating; 1–5 set it.</summary>
public sealed record SetRatingRequest(int Rating);

public sealed record StarredResponse(
    IReadOnlyList<ArtistSummaryDto> Artists,
    IReadOnlyList<AlbumSummaryDto> Albums,
    IReadOnlyList<TrackDto> Tracks);

public sealed record RadioStationDto(Guid Id, string Name, string StreamUrl, string? HomepageUrl);

public sealed record RadioStationListResponse(IReadOnlyList<RadioStationDto> Stations);

public sealed record SaveRadioStationRequest(string Name, string StreamUrl, string? HomepageUrl = null);

/// <summary>
/// A track's lyrics in one call: every stored structured set (synced first), plus best-effort
/// plain text as a fallback when no structured set exists.
/// </summary>
public sealed record TrackLyricsResponse(
    IReadOnlyList<StructuredLyricDto> Structured,
    PlainLyricDto? Plain);
