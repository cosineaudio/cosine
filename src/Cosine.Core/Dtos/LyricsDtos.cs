namespace Cosine.Core.Dtos;

/// <summary>One lyric line. <see cref="Start"/> is a millisecond offset for synced lyrics, null otherwise.</summary>
public record LyricLineDto(int? Start, string Value);

/// <summary>A structured set of lyrics for a track (OpenSubsonic getLyricsBySongId).</summary>
public record StructuredLyricDto(
    string Lang,
    bool Synced,
    int? Offset,
    string? DisplayArtist,
    string? DisplayTitle,
    IReadOnlyList<LyricLineDto> Lines);

/// <summary>Plain, unsynced lyrics keyed by artist + title (legacy Subsonic getLyrics).</summary>
public record PlainLyricDto(string? Artist, string? Title, string Value);
