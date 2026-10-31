using System.Security.Claims;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class BookmarksController : BookmarksControllerBase
{
    private readonly IBookmarkService _bookmarkService;
    private readonly IPlayQueueService _playQueueService;

    public BookmarksController(IBookmarkService bookmarkService, IPlayQueueService playQueueService)
    {
        _bookmarkService = bookmarkService;
        _playQueueService = playQueueService;
    }

    public override async Task<ActionResult<SubsonicResponse>> CreateBookmark(
        string id, int position, string? comment = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var trackId))
            throw new SubsonicException(SubsonicErrorCode._0, "Invalid track ID");

        var userId = GetCurrentUserId();
        await _bookmarkService.CreateBookmarkAsync(userId, trackId, position, comment, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostCreateBookmark(
        Body3 body, CancellationToken cancellationToken = default)
    {
        return CreateBookmark(body.Id, body.Position, body.Comment, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> DeleteBookmark(
        string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var bookmarkId))
            throw new SubsonicException(SubsonicErrorCode._0, "Invalid bookmark ID");

        if (!await _bookmarkService.DeleteBookmarkAsync(bookmarkId, GetCurrentUserId(), cancellationToken))
            throw new SubsonicException(SubsonicErrorCode._70, "Bookmark not found");

        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostDeleteBookmark(
        Body8 body, CancellationToken cancellationToken = default)
    {
        return DeleteBookmark(body.Id, cancellationToken);
    }

    public override async Task<ActionResult<GetBookmarksResponse>> GetBookmarks(
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var bookmarks = await _bookmarkService.GetBookmarksAsync(userId, cancellationToken);

        return Ok(new GetBookmarksResponse
        {
            SubsonicResponse = new GetBookmarksSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Bookmarks = new Generated.Bookmarks
                {
                    Bookmark = bookmarks.Select(MapToBookmark).ToList()
                }
            }
        });
    }

    public override Task<ActionResult<GetBookmarksResponse>> PostGetBookmarks(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetBookmarks(cancellationToken);
    }

    public override async Task<ActionResult<GetPlayQueueResponse>> GetPlayQueue(
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var queue = await _playQueueService.GetPlayQueueAsync(userId, cancellationToken);

        return Ok(new GetPlayQueueResponse
        {
            SubsonicResponse = new GetPlayQueueSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                PlayQueue = MapToPlayQueue(queue)
            }
        });
    }

    public override Task<ActionResult<GetPlayQueueResponse>> PostGetPlayQueue(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetPlayQueue(cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> SavePlayQueue(
        string? id = null, string? current = null, int? position = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        // Subsonic sends one `id` param per song (id=a&id=b&...). The generated
        // signature only binds the first, so read the full ordered list from the request.
        var trackIds = GetQueueTrackIds();

        // Map the currently-playing song id to its index within the ordered queue.
        var currentIndex = 0;
        if (Guid.TryParse(current, out var currentTrackId))
        {
            var idx = trackIds.IndexOf(currentTrackId);
            if (idx >= 0)
                currentIndex = idx;
        }

        await _playQueueService.SavePlayQueueAsync(
            userId, trackIds, currentIndex, position, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostSavePlayQueue(
        Body50? body = null, CancellationToken cancellationToken = default)
    {
        return SavePlayQueue(body?.Id, body?.Current, body?.Position, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> SavePlayQueueByIndex(
        string? id = null, int? currentIndex = null, int? position = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var trackIds = GetQueueTrackIds();

        await _playQueueService.SavePlayQueueAsync(
            userId, trackIds, currentIndex ?? 0, position, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostSavePlayQueueByIndex(
        Body51? body = null, CancellationToken cancellationToken = default)
    {
        int? currentIndex = body?.CurrentIndex is not null && int.TryParse(body.CurrentIndex, out var idx) ? idx : null;
        return SavePlayQueueByIndex(body?.Id, currentIndex, body?.Position, cancellationToken);
    }

    public override async Task<ActionResult<GetPlayQueueByIndexResponse>> GetPlayQueueByIndex(
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var queue = await _playQueueService.GetPlayQueueAsync(userId, cancellationToken);

        return Ok(new GetPlayQueueByIndexResponse
        {
            SubsonicResponse = new GetPlayQueueByIndexSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                PlayQueueByIndex = MapToPlayQueueByIndex(queue)
            }
        });
    }

    public override Task<ActionResult<GetPlayQueueByIndexResponse>> PostGetPlayQueueByIndex(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetPlayQueueByIndex(cancellationToken);
    }

    /// <summary>
    /// Reads the ordered list of queue track ids from the request. Subsonic sends one
    /// <c>id</c> parameter per song, which the generated single-value binding cannot capture.
    /// </summary>
    private List<Guid> GetQueueTrackIds()
    {
        var raw = Request.Query["id"];
        if (raw.Count == 0 && Request.HasFormContentType)
            raw = Request.Form["id"];

        var result = new List<Guid>(raw.Count);
        foreach (var value in raw)
        {
            if (Guid.TryParse(value, out var g))
                result.Add(g);
        }
        return result;
    }

    // Mapping helpers
    private static Generated.Bookmark MapToBookmark(Core.Entities.Bookmark b) => new()
    {
        Position = (int)(b.PositionMs / 1000),
        Comment = b.Comment,
        Created = b.CreatedAt,
        Entry = new Child
        {
            Id = b.Track.Id.ToString(),
            Parent = b.Track.AlbumId.ToString(),
            IsDir = false,
            Title = b.Track.Title,
            Album = b.Track.Album.Name,
            Artist = b.Track.Artist.Name,
            Track = b.Track.TrackNumber,
            Year = b.Track.Year,
            CoverArt = b.Track.AlbumId.ToString(),
            FileSize = b.Track.FileSize,
            ContentType = b.Track.ContentType,
            Suffix = b.Track.Format,
            Duration = b.Track.DurationMs.HasValue ? (int)(b.Track.DurationMs.Value / 1000) : null,
            BitRate = b.Track.BitRate
        }
    };

    private Generated.PlayQueue MapToPlayQueue(Core.Entities.PlayQueue? queue)
    {
        var username = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var tracks = OrderedTracks(queue);

        return new Generated.PlayQueue
        {
            Entry = tracks.Count > 0 ? tracks.Select(MapTrackToChild).ToList() : null,
            Current = CurrentTrackId(queue, tracks),
            Position = (int?)queue?.PositionMs,
            Username = username,
            Changed = queue?.ChangedAt ?? DateTimeOffset.UtcNow,
            ChangedBy = username
        };
    }

    private Generated.PlayQueueByIndex MapToPlayQueueByIndex(Core.Entities.PlayQueue? queue)
    {
        var username = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var tracks = OrderedTracks(queue);

        return new Generated.PlayQueueByIndex
        {
            Entry = tracks.Count > 0 ? tracks.Select(MapTrackToChild).ToList() : null,
            CurrentIndex = tracks.Count > 0 ? queue?.CurrentIndex : null,
            Position = (int?)queue?.PositionMs,
            Username = username,
            Changed = queue?.ChangedAt ?? DateTimeOffset.UtcNow,
            ChangedBy = username
        };
    }

    private static List<Core.Entities.Track> OrderedTracks(Core.Entities.PlayQueue? queue) =>
        queue?.PlayQueueTracks
            .OrderBy(pqt => pqt.Position)
            .Select(pqt => pqt.Track)
            .ToList()
        ?? new List<Core.Entities.Track>();

    private static string? CurrentTrackId(Core.Entities.PlayQueue? queue, List<Core.Entities.Track> tracks)
    {
        if (queue is null || tracks.Count == 0)
            return null;
        var index = Math.Clamp(queue.CurrentIndex, 0, tracks.Count - 1);
        return tracks[index].Id.ToString();
    }

    // Delegates rather than reimplements: these two copies outlived the consolidation
    // SongMapping's summary describes, so the play queue, bookmarks and playlists were still
    // serving songs from a mapper that knew nothing about artist credits.
    private static Child MapTrackToChild(Core.Entities.Track t) => SongMapping.ToChild(t);

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.Parse(sub!);
    }
}
