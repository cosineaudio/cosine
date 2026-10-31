using Cosine.Api.Middleware;
using Cosine.Infrastructure.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

/// <summary>
/// The settings an admin pins to a speaker: what to call it, what to send it, how loud it may
/// get, and whether to offer it at all.
/// <para>
/// Separate from <see cref="AdminController"/> because it shares nothing with it but the policy -
/// and separate from <see cref="PlaybackController"/> because that surface is every listener's
/// and this one is not. The device *list* is not repeated here: it is the same list the picker
/// reads, and a second endpoint returning the same rows would be a second thing to keep true.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/playback")]
[Authorize(Policy = "RequireAdmin")]
[PlaybackExceptionFilter]
public class AdminPlaybackController(
    IPlaybackSessionManager manager,
    ILogger<AdminPlaybackController> logger) : ControllerBase
{
    /// <param name="DisplayName">
    /// Empty clears the override, so the device goes back to the name it advertises. Absent (the
    /// JSON property missing, or null) leaves it alone - which is what lets the page send one
    /// field without restating the others.
    /// </param>
    /// <param name="LyricOffsetMs">
    /// Milliseconds to shift synced lyrics by in the web player while this device is the output.
    /// Positive shows them later, to match a speaker that buffers before it makes a sound.
    /// </param>
    public record UpdateDeviceRequest(
        string? DisplayName = null,
        string? TranscodeProfile = null,
        int? MaxVolumePercent = null,
        int? LyricOffsetMs = null,
        bool? Enabled = null);

    [HttpPut("devices/{id:guid}")]
    public async Task<ActionResult<PlaybackDeviceInfo>> UpdateDevice(
        Guid id, [FromBody] UpdateDeviceRequest request, CancellationToken ct)
    {
        var device = await manager.UpdateDeviceAsync(
            id,
            new PlaybackDeviceSettings(
                request.DisplayName, request.TranscodeProfile, request.MaxVolumePercent,
                request.LyricOffsetMs, request.Enabled),
            ct);

        logger.LogInformation(
            "Admin '{Admin}' updated playback device '{Device}'",
            User.Identity?.Name ?? "unknown", device.DisplayName);

        return Ok(device);
    }
}
