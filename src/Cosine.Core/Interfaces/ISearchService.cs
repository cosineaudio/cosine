using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public record SearchResults(IReadOnlyList<Artist> Artists, IReadOnlyList<Album> Albums, IReadOnlyList<Track> Tracks);

public interface ISearchService
{
    Task<SearchResults> SearchAsync(string query, int artistCount = 20, int albumCount = 20, int trackCount = 40, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the whole library in a stable order with independent per-type paging.
    /// Backs the OpenSubsonic "empty query returns everything" search behavior that
    /// clients use for full library sync.
    /// </summary>
    Task<SearchResults> BrowseAllAsync(
        int artistCount, int artistOffset,
        int albumCount, int albumOffset,
        int trackCount, int trackOffset,
        CancellationToken ct = default);
    Task RebuildSearchIndexAsync(string? entityType = null, Guid? entityId = null, CancellationToken ct = default);
    Task RebuildArtistAlbumIndexAsync(CancellationToken ct = default);
    Task RebuildSearchIndexForTracksAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default);
}
