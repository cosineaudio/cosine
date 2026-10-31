namespace Cosine.Core.Entities;

public class AlbumGenre
{
    public Guid AlbumId { get; set; }
    public Guid GenreId { get; set; }

    public Album Album { get; set; } = null!;
    public Genre Genre { get; set; } = null!;
}
