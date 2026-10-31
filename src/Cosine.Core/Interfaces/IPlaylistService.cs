using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public interface IPlaylistService
{
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(Guid userId, CancellationToken ct = default);
    Task<Playlist?> GetPlaylistAsync(Guid id, CancellationToken ct = default);
    Task<Playlist> CreatePlaylistAsync(string name, Guid userId, CancellationToken ct = default);
    Task DeletePlaylistAsync(Guid id, CancellationToken ct = default);
    Task UpdatePlaylistAsync(Guid id, string? name = null, string? comment = null, bool? isPublic = null, CancellationToken ct = default);
    Task AddTracksAsync(Guid id, Guid[] trackIds, CancellationToken ct = default);
    Task RemoveTrackAsync(Guid id, Guid trackId, CancellationToken ct = default);

    /// <summary>
    /// Removes one entry by its own id (<see cref="PlaylistTrack.Id"/>). The web API's removal
    /// path: unlike a position index it cannot drift when the playlist changes underneath the
    /// caller, and unlike a track id it names one entry, not "whichever row holds that track".
    /// No-op when the entry does not exist or belongs to another playlist.
    /// </summary>
    Task RemoveEntryAsync(Guid id, Guid entryId, CancellationToken ct = default);

    /// <summary>Removes every entry, leaving the (empty) playlist in place.</summary>
    Task ClearTracksAsync(Guid id, CancellationToken ct = default);

    Task ReorderAsync(Guid id, Guid[] trackIds, CancellationToken ct = default);
}
