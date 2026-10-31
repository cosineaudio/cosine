using Cosine.Api.Generated;
using Cosine.Core.Interfaces;

namespace Cosine.Api.Controllers;

/// <summary>
/// Stamps <c>userRating</c> onto an already-built response.
/// <para>
/// This is deliberately a *response* pass rather than part of the mapping helpers, because the
/// library objects those helpers read come out of <c>LibraryCache</c> and are shared across every
/// user. A per-user field written into a cached payload leaks between accounts. <c>starCount</c>
/// is a global count and is safely cacheable; <c>userRating</c> is not, so it is joined on here,
/// after the cached object has come back.
/// </para>
/// <para>
/// Collect the nodes to stamp with the <c>Add</c>/<c>AddRange</c> overloads, then call
/// <see cref="ApplyAsync"/> once - it issues at most one query per item type however many nodes
/// were collected, and none at all when nothing was.
/// </para>
/// </summary>
public sealed class RatingOverlay
{
    private readonly Dictionary<string, List<(Guid Id, Action<int> Assign)>> _pending = [];

    /// <summary>
    /// <c>Child</c> covers both songs and albums in the Subsonic schema, and <c>isDir</c> is
    /// exactly that distinction - every mapping helper in this project already sets it.
    /// </summary>
    public RatingOverlay Add(Child? child)
    {
        if (child is not null && Guid.TryParse(child.Id, out var id))
            Register(child.IsDir ? IRatingService.Album : IRatingService.Track,
                id, v => child.UserRating = v);
        return this;
    }

    public RatingOverlay AddRange(IEnumerable<Child>? children)
    {
        if (children is not null)
            foreach (var c in children) Add(c);
        return this;
    }

    public RatingOverlay Add(AlbumID3? album)
    {
        if (album is not null && Guid.TryParse(album.Id, out var id))
            Register(IRatingService.Album, id, v => album.UserRating = v);
        return this;
    }

    public RatingOverlay AddRange(IEnumerable<AlbumID3>? albums)
    {
        if (albums is not null)
            foreach (var a in albums) Add(a);
        return this;
    }

    /// <summary>
    /// <c>ArtistID3</c> carries no <c>userRating</c> field in the Subsonic schema, so only the
    /// older <c>Artist</c> element (getIndexes, getStarred) can show an artist's rating.
    /// </summary>
    public RatingOverlay Add(Generated.Artist? artist)
    {
        if (artist is not null && Guid.TryParse(artist.Id, out var id))
            Register(IRatingService.Artist, id, v => artist.UserRating = v);
        return this;
    }

    public RatingOverlay AddRange(IEnumerable<Generated.Artist>? artists)
    {
        if (artists is not null)
            foreach (var a in artists) Add(a);
        return this;
    }

    /// <summary>
    /// A <c>getMusicDirectory</c> directory is itself either an album or an artist;
    /// <paramref name="itemType"/> says which.
    /// </summary>
    public RatingOverlay Add(Generated.Directory? directory, string itemType)
    {
        if (directory is not null && Guid.TryParse(directory.Id, out var id))
            Register(itemType, id, v => directory.UserRating = v);
        return this;
    }

    public async Task ApplyAsync(IRatingService ratings, Guid userId, CancellationToken ct = default)
    {
        foreach (var (itemType, entries) in _pending)
        {
            var ids = entries.Select(e => e.Id).Distinct().ToList();
            var values = await ratings.GetRatingsAsync(userId, itemType, ids, ct);
            if (values.Count == 0)
                continue;

            foreach (var (id, assign) in entries)
                if (values.TryGetValue(id, out var value))
                    assign(value);
        }
    }

    private void Register(string itemType, Guid id, Action<int> assign)
    {
        if (!_pending.TryGetValue(itemType, out var entries))
            _pending[itemType] = entries = [];
        entries.Add((id, assign));
    }
}
