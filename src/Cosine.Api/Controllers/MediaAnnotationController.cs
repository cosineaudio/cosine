using System.Security.Claims;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class MediaAnnotationController : Media_AnnotationControllerBase
{
    private readonly IStarService _starService;
    private readonly IRatingService _ratingService;
    private readonly IPlayHistoryService _playHistoryService;
    private readonly INowPlayingService _nowPlaying;

    public MediaAnnotationController(
        IStarService starService,
        IRatingService ratingService,
        IPlayHistoryService playHistoryService,
        INowPlayingService nowPlaying)
    {
        _starService = starService;
        _ratingService = ratingService;
        _playHistoryService = playHistoryService;
        _nowPlaying = nowPlaying;
    }

    public override async Task<ActionResult<SubsonicResponse>> Star(
        IEnumerable<string>? id = null, IEnumerable<string>? albumId = null,
        IEnumerable<string>? artistId = null, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var starred = 0;

        if (id is not null)
        {
            foreach (var s in id)
            {
                if (Guid.TryParse(s, out var guid))
                {
                    await _starService.StarAsync(userId, "track", guid, cancellationToken);
                    starred++;
                }
            }
        }

        if (albumId is not null)
        {
            foreach (var s in albumId)
            {
                if (Guid.TryParse(s, out var guid))
                {
                    await _starService.StarAsync(userId, "album", guid, cancellationToken);
                    starred++;
                }
            }
        }

        if (artistId is not null)
        {
            foreach (var s in artistId)
            {
                if (Guid.TryParse(s, out var guid))
                {
                    await _starService.StarAsync(userId, "artist", guid, cancellationToken);
                    starred++;
                }
            }
        }

        if (starred == 0)
            throw new SubsonicException(SubsonicErrorCode._0, "No valid IDs provided");

        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostStar(
        Body57 body, CancellationToken cancellationToken = default)
    {
        return Star(body.Id, body.AlbumId, body.ArtistId, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> Unstar(
        IEnumerable<string>? id = null, IEnumerable<string>? albumId = null,
        IEnumerable<string>? artistId = null, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var unstarred = 0;

        if (id is not null)
        {
            foreach (var s in id)
            {
                if (Guid.TryParse(s, out var guid))
                {
                    await _starService.UnstarAsync(userId, "track", guid, cancellationToken);
                    unstarred++;
                }
            }
        }

        if (albumId is not null)
        {
            foreach (var s in albumId)
            {
                if (Guid.TryParse(s, out var guid))
                {
                    await _starService.UnstarAsync(userId, "album", guid, cancellationToken);
                    unstarred++;
                }
            }
        }

        if (artistId is not null)
        {
            foreach (var s in artistId)
            {
                if (Guid.TryParse(s, out var guid))
                {
                    await _starService.UnstarAsync(userId, "artist", guid, cancellationToken);
                    unstarred++;
                }
            }
        }

        if (unstarred == 0)
            throw new SubsonicException(SubsonicErrorCode._0, "No valid IDs provided");

        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostUnstar(
        Body59 body, CancellationToken cancellationToken = default)
    {
        return Unstar(
            ConvertToEnumerable(body.Id),
            ConvertToEnumerable(body.AlbumId),
            ConvertToEnumerable(body.ArtistId),
            cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> Scrobble(
        string id, int? time = null, bool? submission = true,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._0, "Invalid track ID");

        var userId = GetCurrentUserId();

        // submission=true is the deliberate "this counts as a play" signal - the only thing
        // that increments a play count. submission=false is a now-playing notification.
        if (submission == true)
        {
            await _playHistoryService.RecordPlayAsync(userId, guid, cancellationToken);
        }

        await _nowPlaying.ReportAsync(userId, CurrentUsername(), guid, ct: cancellationToken);

        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostScrobble(
        Body52 body, CancellationToken cancellationToken = default)
    {
        return Scrobble(body.Id, body.Time, body.Submission, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> SetRating(
        string id, int rating, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._0, "Invalid ID");

        if (rating < 0 || rating > 5)
            throw new SubsonicException(SubsonicErrorCode._0, "Rating must be between 0 and 5");

        var userId = GetCurrentUserId();

        // Ratings and stars are independent Subsonic concepts. This used to star on 4-5 and
        // unstar on 1-3, because there was nowhere to put the value - now that `Rating` exists
        // the value is stored and the star is left alone. Anyone who was using ratings as a
        // star proxy will see that change.
        var itemType = await _ratingService.SetRatingAsync(userId, guid, rating, cancellationToken);
        if (itemType is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Not found");

        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostSetRating(
        Body56 body, CancellationToken cancellationToken = default)
    {
        return SetRating(body.Id, body.Rating, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> ReportPlayback(
        string mediaId, MediaType4 mediaType, int positionMs, State state,
        float? playbackRate = 1F, bool? ignoreScrobble = false,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(mediaId, out var guid))
            throw new SubsonicException(SubsonicErrorCode._0, "Invalid media ID");

        var userId = GetCurrentUserId();

        // reportPlayback is a *progress* signal: clients send it on every play, unpause and
        // seek. Recording a play here counted each unpause as another listen. Play counts
        // come from scrobble, which is the deliberate signal (see Scrobble above); this
        // endpoint only maintains now-playing state.
        await _nowPlaying.ReportAsync(
            userId, CurrentUsername(), guid, positionMs, MapState(state), cancellationToken);

        return Ok();
    }

    private static string MapState(State state) => state switch
    {
        State.Paused => INowPlayingService.StatePaused,
        State.Stopped => INowPlayingService.StateStopped,
        _ => INowPlayingService.StatePlaying
    };

    public override Task<ActionResult<SubsonicResponse>> PostReportPlayback(
        Body49 body, CancellationToken cancellationToken = default)
    {
        return ReportPlayback(body.MediaId,
            (MediaType4)(int)body.MediaType, body.PositionMs,
            (State)(int)body.State, body.PlaybackRate, body.IgnoreScrobble, cancellationToken);
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.Parse(sub!);
    }

    // Both auth paths issue ClaimTypes.Name; there is no "username" claim (see #18).
    private string CurrentUsername() => User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    private static IEnumerable<string>? ConvertToEnumerable(object? value)
    {
        if (value is null) return null;
        if (value is IEnumerable<string> strings) return strings;
        if (value is string single) return new[] { single };
        // Try parsing as JSON array
        if (value is System.Text.Json.JsonElement json)
        {
            if (json.ValueKind == System.Text.Json.JsonValueKind.Array)
                return json.EnumerateArray().Select(e => e.GetString()!).ToList();
            if (json.ValueKind == System.Text.Json.JsonValueKind.String)
                return new[] { json.GetString()! };
        }
        return null;
    }
}
