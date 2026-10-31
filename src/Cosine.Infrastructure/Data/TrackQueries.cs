using Cosine.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Cosine.Infrastructure.Data;

/// <summary>
/// The navigations a song response needs loaded.
/// <para>
/// This exists because "the query forgot an Include" fails silently here. Every song-returning
/// endpoint maps through one shared mapper, which falls back to the primary artist when the
/// credit is absent - so a query that omits <c>TrackArtists</c> does not throw, it just quietly
/// serves the old single-artist answer on that one surface. Nine call sites across seven
/// services load tracks for that mapper; spelling the chain out nine times is how eight of them
/// end up right and one does not.
/// </para>
/// </summary>
public static class TrackQueries
{
    /// <summary>Artist, album (with its artist), and the full artist credit in credit order.</summary>
    public static IQueryable<Track> WithSongMetadata(this IQueryable<Track> tracks) => tracks
        .Include(t => t.Artist)
        .Include(t => t.Album).ThenInclude(a => a.Artist)
        .IncludeCredits();

    /// <summary>
    /// The credit alone, for queries that already have their own idea of which other
    /// navigations to load.
    /// </summary>
    public static IQueryable<Track> IncludeCredits(this IQueryable<Track> tracks) => tracks
        .Include(t => t.TrackArtists.OrderBy(ta => ta.Ordinal))
        .ThenInclude(ta => ta.Artist);

    // Queries that reach tracks through a parent - a playlist, the play queue, a bookmark -
    // spell the chain out at the call site, in the same repeated-Include style those queries
    // already use for Artist and Album. A generic ThenInclude helper for them costs more to
    // read than the two lines it saves.
}
