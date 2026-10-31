using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public record StarredItems(IReadOnlyList<Artist> Artists, IReadOnlyList<Album> Albums, IReadOnlyList<Track> Tracks);

public interface IStarService
{
    Task<StarredItems> GetStarredAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// One page of the user's starred albums, newest star first. <c>getAlbumList?type=starred</c>
    /// used to serve a page by loading every starred item of all three types and slicing in memory.
    /// </summary>
    Task<IReadOnlyList<Album>> GetStarredAlbumsAsync(Guid userId, int offset, int limit, CancellationToken ct = default);
    Task StarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default);
    Task UnstarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default);
}
