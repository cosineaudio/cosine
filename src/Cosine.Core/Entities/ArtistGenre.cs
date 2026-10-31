namespace Cosine.Core.Entities;

public class ArtistGenre
{
    public Guid ArtistId { get; set; }
    public Guid GenreId { get; set; }

    public Artist Artist { get; set; } = null!;
    public Genre Genre { get; set; } = null!;
}
