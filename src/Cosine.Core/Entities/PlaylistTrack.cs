namespace Cosine.Core.Entities;

public class PlaylistTrack
{
    /// <summary>
    /// Stable identity of this playlist entry, exposed by the web API so removal names the
    /// entry itself instead of a (position-index) or (track-id) proxy. Not the primary key -
    /// the composite (PlaylistId, TrackId) key predates it and still holds.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlaylistId { get; set; }
    public Guid TrackId { get; set; }
    public int Position { get; set; }

    public Playlist Playlist { get; set; } = null!;
    public Track Track { get; set; } = null!;
}
