using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

[Route("api/now-playing")]
public class NowPlayingController(
    INowPlayingService nowPlaying,
    IMediaLibraryService library,
    IRatingService ratings) : WebApiController
{
    /// <summary>
    /// The caller's own live playback sessions. Playback is private to each user - this never
    /// shows what other accounts are listening to, same as the Subsonic surface.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<NowPlayingResponse>> GetNowPlaying(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = CurrentUserId;
        var entries = new List<NowPlayingEntryDto>();

        foreach (var report in nowPlaying.GetCurrent())
        {
            if (report.UserId != userId)
                continue;

            // A report can outlive its track (removed by a scan between the two); skip it
            // rather than emitting an entry with no metadata.
            var track = await library.GetTrackAsync(report.TrackId, ct);
            if (track is null)
                continue;

            entries.Add(new NowPlayingEntryDto(
                WebDtoMapper.ToDto(track),
                report.Username,
                (int)Math.Max(0, (now - report.ReportedAt).TotalMinutes),
                report.PositionMs,
                report.State));
        }

        await ApplyAsync(new UserDataOverlay().AddRange(entries.Select(e => e.Track)), ratings, ct);
        return new NowPlayingResponse(entries);
    }
}
