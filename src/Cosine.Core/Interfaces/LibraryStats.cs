namespace Cosine.Core.Interfaces;

public record LibraryStats(int ArtistCount, int AlbumCount, int TrackCount, long TotalSizeBytes, long TotalDurationMs);

/// <summary>A genre plus its derived song/album counts (both counted through the TrackGenre join).</summary>
public record GenreCount(string Name, int SongCount, int AlbumCount);
