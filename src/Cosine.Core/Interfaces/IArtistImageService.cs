namespace Cosine.Core.Interfaces;

public interface IArtistImageService
{
    /// <summary>
    /// Fetches images for artists that have none yet and stores them on the artist.
    /// Returns the number of artists that received an image.
    /// </summary>
    Task<int> FetchMissingImagesAsync(CancellationToken ct = default);
}
