namespace Cosine.Api.WebApi.Dtos;

public sealed record GenreDto(string Name, int TrackCount, int AlbumCount);

public sealed record GenreListResponse(IReadOnlyList<GenreDto> Genres);

public sealed record TrackListResponse(IReadOnlyList<TrackDto> Tracks);

public sealed record AlbumListResponse(IReadOnlyList<AlbumSummaryDto> Albums);

public sealed record SearchResponse(
    IReadOnlyList<ArtistSummaryDto> Artists,
    IReadOnlyList<AlbumSummaryDto> Albums,
    IReadOnlyList<TrackDto> Tracks);

/// <summary>One of the caller's own active playback sessions.</summary>
public sealed record NowPlayingEntryDto(
    TrackDto Track,
    string Username,
    int MinutesAgo,
    int? PositionMs,
    string State);

public sealed record NowPlayingResponse(IReadOnlyList<NowPlayingEntryDto> Entries);

public sealed record TranscodeFormatDto(string Name, string Extension, IReadOnlyList<int> BitRates);

public sealed record TranscodeFormatsResponse(IReadOnlyList<TranscodeFormatDto> Formats);
