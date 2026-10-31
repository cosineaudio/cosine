using System.Security.Claims;
using Cosine.Api.Middleware;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

/// <summary>
/// Command surface for remote playback. Plain JSON: the Subsonic output formatter and filters
/// are registered globally but self-scope to <c>/rest</c>, so nothing here is enveloped.
/// </summary>
[ApiController]
[Route("playback")]
[Authorize]
[PlaybackExceptionFilter]
public class PlaybackController(IPlaybackSessionManager manager) : ControllerBase
{
    private PlaybackCaller Caller => new(
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
        User.FindFirstValue("IsAdmin") == "True");

    [HttpGet("devices")]
    public async Task<ActionResult<IReadOnlyList<PlaybackDeviceInfo>>> GetDevices(CancellationToken ct) =>
        Ok(await manager.GetDevicesAsync(ct));

    public record StartSessionRequest(Guid DeviceId, bool TakeOver = false);

    [HttpPost("sessions")]
    public async Task<ActionResult<PlaybackStateSnapshot>> StartSession(
        [FromBody] StartSessionRequest request, CancellationToken ct) =>
        Ok(await manager.StartSessionAsync(request.DeviceId, Caller, request.TakeOver, ct));

    [HttpGet("sessions/{id:guid}")]
    public ActionResult<PlaybackStateSnapshot> GetSession(Guid id) =>
        manager.TryGetState(id) is { } state ? Ok(state) : NotFound();

    [HttpGet("sessions/{id:guid}/queue")]
    public ActionResult<PlaybackQueueSnapshot> GetQueue(Guid id) =>
        manager.TryGetQueue(id) is { } queue ? Ok(queue) : NotFound();

    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> EndSession(Guid id, CancellationToken ct)
    {
        await manager.EndSessionAsync(id, Caller, ct);
        return NoContent();
    }

    // ---- Queue ----

    public record SetQueueRequest(IReadOnlyList<Guid> TrackIds, int StartIndex = 0);

    /// <summary>Replace the queue and start playing at the given index.</summary>
    [HttpPut("sessions/{id:guid}/queue")]
    public async Task<ActionResult<PlaybackStateSnapshot>> SetQueue(
        Guid id, [FromBody] SetQueueRequest request, CancellationToken ct) =>
        Ok(await manager.SetQueueAsync(id, Caller, request.TrackIds, request.StartIndex, ct));

    public record AddToQueueRequest(IReadOnlyList<Guid> TrackIds, int? InsertAt = null);

    [HttpPost("sessions/{id:guid}/queue")]
    public async Task<ActionResult<PlaybackStateSnapshot>> AddToQueue(
        Guid id, [FromBody] AddToQueueRequest request, CancellationToken ct) =>
        Ok(await manager.AddToQueueAsync(id, Caller, request.TrackIds, request.InsertAt, ct));

    [HttpDelete("sessions/{id:guid}/queue/{position:int}")]
    public async Task<ActionResult<PlaybackStateSnapshot>> RemoveFromQueue(
        Guid id, int position, CancellationToken ct) =>
        Ok(await manager.RemoveFromQueueAsync(id, Caller, position, ct));

    public record MoveInQueueRequest(int From, int To);

    [HttpPost("sessions/{id:guid}/queue/move")]
    public async Task<ActionResult<PlaybackStateSnapshot>> MoveInQueue(
        Guid id, [FromBody] MoveInQueueRequest request, CancellationToken ct) =>
        Ok(await manager.MoveInQueueAsync(id, Caller, request.From, request.To, ct));

    /// <summary>Play the queue entry at <paramref name="position"/> - a click in the queue panel.</summary>
    [HttpPost("sessions/{id:guid}/queue/{position:int}/play")]
    public async Task<ActionResult<PlaybackStateSnapshot>> PlayAt(
        Guid id, int position, CancellationToken ct) =>
        Ok(await manager.PlayAtAsync(id, Caller, position, ct));

    // ---- Transport ----

    [HttpPost("sessions/{id:guid}/play")]
    public async Task<ActionResult<PlaybackStateSnapshot>> Play(Guid id, CancellationToken ct) =>
        Ok(await manager.PlayAsync(id, Caller, ct));

    [HttpPost("sessions/{id:guid}/pause")]
    public async Task<ActionResult<PlaybackStateSnapshot>> Pause(Guid id, CancellationToken ct) =>
        Ok(await manager.PauseAsync(id, Caller, ct));

    [HttpPost("sessions/{id:guid}/stop")]
    public async Task<ActionResult<PlaybackStateSnapshot>> Stop(Guid id, CancellationToken ct) =>
        Ok(await manager.StopAsync(id, Caller, ct));

    [HttpPost("sessions/{id:guid}/next")]
    public async Task<ActionResult<PlaybackStateSnapshot>> Next(Guid id, CancellationToken ct) =>
        Ok(await manager.NextAsync(id, Caller, ct));

    [HttpPost("sessions/{id:guid}/previous")]
    public async Task<ActionResult<PlaybackStateSnapshot>> Previous(Guid id, CancellationToken ct) =>
        Ok(await manager.PreviousAsync(id, Caller, ct));

    public record SeekRequest(double PositionSeconds);

    [HttpPost("sessions/{id:guid}/seek")]
    public async Task<ActionResult<PlaybackStateSnapshot>> Seek(
        Guid id, [FromBody] SeekRequest request, CancellationToken ct) =>
        Ok(await manager.SeekAsync(id, Caller, TimeSpan.FromSeconds(request.PositionSeconds), ct));

    public record VolumeRequest(int Percent);

    [HttpPost("sessions/{id:guid}/volume")]
    public async Task<ActionResult<PlaybackStateSnapshot>> SetVolume(
        Guid id, [FromBody] VolumeRequest request, CancellationToken ct) =>
        Ok(await manager.SetVolumeAsync(id, Caller, request.Percent, ct));

    public record RepeatRequest(PlaybackRepeatMode Mode);

    [HttpPost("sessions/{id:guid}/repeat")]
    public async Task<ActionResult<PlaybackStateSnapshot>> SetRepeat(
        Guid id, [FromBody] RepeatRequest request, CancellationToken ct) =>
        Ok(await manager.SetRepeatAsync(id, Caller, request.Mode, ct));

    public record ShuffleRequest(bool Shuffled);

    [HttpPost("sessions/{id:guid}/shuffle")]
    public async Task<ActionResult<PlaybackStateSnapshot>> SetShuffle(
        Guid id, [FromBody] ShuffleRequest request, CancellationToken ct) =>
        Ok(await manager.SetShuffleAsync(id, Caller, request.Shuffled, ct));

    public record AutoPlayRequest(bool Enabled);

    /// <summary>
    /// Keep the queue going once it runs out, seeded from what has been played. The session owns
    /// the setting, so the speaker carries on with no browser attached.
    /// </summary>
    [HttpPost("sessions/{id:guid}/autoplay")]
    public async Task<ActionResult<PlaybackStateSnapshot>> SetAutoPlay(
        Guid id, [FromBody] AutoPlayRequest request, CancellationToken ct) =>
        Ok(await manager.SetAutoPlayAsync(id, Caller, request.Enabled, ct));
}
