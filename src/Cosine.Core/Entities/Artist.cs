namespace Cosine.Core.Entities;

public class Artist
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SortName { get; set; } = string.Empty;
    public string? MusicBrainzId { get; set; }
    public byte[]? ImageBlob { get; set; }
    public string? Biography { get; set; }
    public int StarCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Album> Albums { get; set; } = new List<Album>();
    public ICollection<Track> Tracks { get; set; } = new List<Track>();
    public ICollection<TrackArtist> TrackArtists { get; set; } = new List<TrackArtist>();
    public ICollection<ArtistGenre> ArtistGenres { get; set; } = new List<ArtistGenre>();
}
