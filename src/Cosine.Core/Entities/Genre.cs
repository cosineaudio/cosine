namespace Cosine.Core.Entities;

public class Genre
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<TrackGenre> TrackGenres { get; set; } = new List<TrackGenre>();
    public ICollection<AlbumGenre> AlbumGenres { get; set; } = new List<AlbumGenre>();
    public ICollection<ArtistGenre> ArtistGenres { get; set; } = new List<ArtistGenre>();
}
