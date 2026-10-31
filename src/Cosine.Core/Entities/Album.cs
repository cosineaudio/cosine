namespace Cosine.Core.Entities;

public class Album
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SortName { get; set; } = string.Empty;
    public Guid ArtistId { get; set; }
    public int? Year { get; set; }
    // MusicBrainz release-group primary type (album/single/ep/broadcast/other),
    // captured from tags at scan time. Null when untagged. Drives Release Radar's
    // single-vs-album classification.
    public string? ReleaseType { get; set; }
    public string? MusicBrainzId { get; set; }
    public byte[]? CoverBlob { get; set; }
    public long DurationMs { get; set; }
    public int TrackCount { get; set; }
    public int StarCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Artist Artist { get; set; } = null!;
    public ICollection<Track> Tracks { get; set; } = new List<Track>();
    public ICollection<AlbumGenre> AlbumGenres { get; set; } = new List<AlbumGenre>();
}
