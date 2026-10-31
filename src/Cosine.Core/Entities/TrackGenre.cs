namespace Cosine.Core.Entities;

public class TrackGenre
{
    public Guid TrackId { get; set; }
    public Guid GenreId { get; set; }

    public Track Track { get; set; } = null!;
    public Genre Genre { get; set; } = null!;
}
