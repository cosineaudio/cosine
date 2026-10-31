using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>The caller's saved play queue - what the player restores on app start.</summary>
[Route("api/queue")]
public class QueueController(
    IPlayQueueService playQueue,
    IRatingService ratings) : WebApiController
{
    /// <summary>204 when no queue is saved - a saved-but-empty queue and "none" read the same to the player.</summary>
    [HttpGet]
    public async Task<ActionResult<QueueDto>> GetQueue(CancellationToken ct)
    {
        var queue = await playQueue.GetPlayQueueAsync(CurrentUserId, ct);
        if (queue is null || queue.PlayQueueTracks.Count == 0)
            return NoContent();

        var ordered = queue.PlayQueueTracks.OrderBy(qt => qt.Position).ToList();
        var tracks = ordered.Select(qt => WebDtoMapper.ToDto(qt.Track)).ToList();
        var currentTrackId = queue.CurrentIndex >= 0 && queue.CurrentIndex < tracks.Count
            ? tracks[queue.CurrentIndex].Id
            : (Guid?)null;

        await ApplyAsync(new UserDataOverlay().AddRange(tracks), ratings, ct);
        return new QueueDto(tracks, currentTrackId, queue.PositionMs);
    }

    [HttpPut]
    public async Task<IActionResult> SaveQueue(
        [FromBody] SaveQueueRequest request, CancellationToken ct)
    {
        var trackIds = request.TrackIds ?? [];

        // The current track is named by id; its first occurrence in the list is the index the
        // storage keeps. An id that is not in the list falls back to the head.
        var currentIndex = 0;
        if (request.CurrentTrackId is { } current)
        {
            var idx = trackIds.ToList().IndexOf(current);
            if (idx >= 0)
                currentIndex = idx;
        }

        await playQueue.SavePlayQueueAsync(
            CurrentUserId, trackIds.ToList(), currentIndex, request.PositionMs, ct);
        return NoContent();
    }
}
