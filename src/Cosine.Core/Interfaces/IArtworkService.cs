namespace Cosine.Core.Interfaces;

public interface IArtworkService
{
    Task<(byte[] Bytes, string ContentType)?> GetAlbumArtAsync(Guid albumId, int? size = null, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)?> GetArtistArtAsync(Guid artistId, int? size = null, CancellationToken ct = default);
}
