using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public interface IMediaLibraryService
{
    Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default);
    Task<Artist?> GetArtistAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsAsync(Guid? artistId = null, CancellationToken ct = default);
    Task<Album?> GetAlbumAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(Guid albumId, CancellationToken ct = default);

    /// <summary>
    /// Every track belonging to an artist, in album order - the tracks on their own albums plus
    /// the ones credited to them on someone else's, deduplicated. Backs the artist page's
    /// Play/Shuffle buttons, which otherwise have to fan out one <c>getAlbum</c> per album.
    /// </summary>
    Task<IReadOnlyList<Track>> GetArtistSongsAsync(Guid artistId, CancellationToken ct = default);
    Task<Track?> GetTrackAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<GenreCount>> GetGenresAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetRecentlyAddedAsync(int limit = 50, int offset = 0, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetRandomAsync(int limit = 20, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsAlphabeticalAsync(bool byArtist, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsByYearAsync(int fromYear, int toYear, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsByGenreAsync(string genre, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetTracksByGenreAsync(string genre, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetMostStarredAlbumsAsync(int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetRandomTracksAsync(int limit = 20, CancellationToken ct = default);
    Task<LibraryStats> GetLibraryStatsAsync(CancellationToken ct = default);
}
