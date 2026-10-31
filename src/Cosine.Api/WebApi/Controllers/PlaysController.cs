using Cosine.Api.WebApi.Dtos;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// The web player's play reporting - the feed for now-playing, the activity hub, and play
/// history/stats. Replaces the client's use of Subsonic <c>scrobble</c>/<c>reportPlayback</c>
/// with one endpoint: <c>countPlay</c> carries the deliberate "this counts as a listen"
/// signal, everything else is progress that only maintains now-playing state. Keeping the
/// split exactly as the Subsonic surface has it is what stops unpauses from inflating play
/// counts.
/// </summary>
[Route("api/plays")]
public class PlaysController(
    IPlayHistoryService playHistory,
    INowPlayingService nowPlaying) : WebApiController
{
    [HttpPost]
    public async Task<IActionResult> Report(
        [FromBody] PlayReportRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId;

        if (request.CountPlay)
            await playHistory.RecordPlayAsync(userId, request.TrackId, ct);

        await nowPlaying.ReportAsync(
            userId, CurrentUsername, request.TrackId, request.PositionMs,
            NormalizeState(request.State), ct);

        return NoContent();
    }

    private static string NormalizeState(string? state) => state switch
    {
        INowPlayingService.StatePaused => INowPlayingService.StatePaused,
        INowPlayingService.StateStopped => INowPlayingService.StateStopped,
        _ => INowPlayingService.StatePlaying,
    };
}
