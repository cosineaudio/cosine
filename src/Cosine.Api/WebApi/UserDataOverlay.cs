using Cosine.Api.WebApi.Dtos;
using Cosine.Core.Interfaces;

namespace Cosine.Api.WebApi;

/// <summary>
/// Stamps <c>userRating</c> onto already-mapped web DTOs - the web-API analog of
/// <c>RatingOverlay</c>, and it exists for the same reason: the entities the mapper reads come
/// out of <c>LibraryCache</c> and are shared across every user, so a per-user field written
/// during mapping would leak between accounts. This runs as a response pass, after the cached
/// object has come back and been copied into a fresh DTO.
/// <para>
/// Collect nodes with <c>Add</c>/<c>AddRange</c>, then call <see cref="ApplyAsync"/> once - at
/// most one ratings query per item type, none when nothing was collected.
/// </para>
/// </summary>
public sealed class UserDataOverlay
{
    private readonly Dictionary<string, List<(Guid Id, Action<int> Assign)>> _pending = [];

    public UserDataOverlay Add(TrackDto? track)
    {
        if (track is not null)
            Register(IRatingService.Track, track.Id, v => track.UserRating = v);
        return this;
    }

    public UserDataOverlay AddRange(IEnumerable<TrackDto>? tracks)
    {
        if (tracks is not null)
            foreach (var t in tracks) Add(t);
        return this;
    }

    public UserDataOverlay Add(AlbumSummaryDto? album)
    {
        if (album is not null)
            Register(IRatingService.Album, album.Id, v => album.UserRating = v);
        return this;
    }

    public UserDataOverlay AddRange(IEnumerable<AlbumSummaryDto>? albums)
    {
        if (albums is not null)
            foreach (var a in albums) Add(a);
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
