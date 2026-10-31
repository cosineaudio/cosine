namespace Cosine.Infrastructure.Playback;

/// <summary>
/// Base for the failures a playback controller must turn into a specific status code. Nothing
/// global handles exceptions on non-<c>/rest</c> paths, so these are caught explicitly.
/// </summary>
public abstract class PlaybackException(string message) : Exception(message);

/// <summary>
/// <c>Playback:AdvertisedBaseUrl</c> is unset, so no URL can be built that a speaker could
/// fetch. Thrown when a session is created rather than at startup: the rest of cosine works
/// fine without it, and refusing to boot over an unconfigured optional feature would take a
/// working install down on upgrade.
/// </summary>
public sealed class PlaybackNotConfiguredException()
    : PlaybackException(
        "Remote playback is unavailable: Playback:AdvertisedBaseUrl is not set. Set it to a URL " +
        "the speakers can reach on the LAN, e.g. http://192.168.1.10:8080.");

public sealed class PlaybackSessionNotFoundException(Guid sessionId)
    : PlaybackException($"No live playback session {sessionId}.");

public sealed class PlaybackDeviceNotFoundException(Guid deviceId)
    : PlaybackException($"No playback device {deviceId}.");

/// <summary>
/// Another user holds the device. Carries who, so the UI can offer a takeover with a name on
/// it instead of a bare 409.
/// </summary>
public sealed class PlaybackDeviceBusyException(Guid deviceId, Guid ownerUserId, string ownerUsername)
    : PlaybackException($"Device {deviceId} is in use by {ownerUsername}.")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid OwnerUserId { get; } = ownerUserId;
    public string OwnerUsername { get; } = ownerUsername;
}

/// <summary>
/// cosine has a row for this device but has not seen it on the network since booting, so there
/// is no current address to connect to. Distinct from "no such device": the fix is to turn the
/// speaker on or check host networking, not to look for a different device.
/// </summary>
public sealed class PlaybackDeviceOfflineException(Guid deviceId, string displayName)
    : PlaybackException($"Device \"{displayName}\" has not been seen on the network.")
{
    public Guid DeviceId { get; } = deviceId;
}

/// <summary>
/// An admin has switched this device off. Separate from offline: the speaker may be sitting
/// there answering announcements perfectly well, and the fix is in cosine rather than in the room.
/// </summary>
public sealed class PlaybackDeviceDisabledException(Guid deviceId, string displayName)
    : PlaybackException($"Device \"{displayName}\" is disabled in cosine.")
{
    public Guid DeviceId { get; } = deviceId;
}

/// <summary>
/// A device setting cosine cannot honour - a volume ceiling outside 0–100, a transcode profile
/// naming a format there is no encoder for. Refused at the point it is set rather than stored and
/// discovered later, because "later" is a speaker that goes silent mid-queue with the reason
/// three days behind it in a log.
/// </summary>
public sealed class PlaybackSettingInvalidException(string message) : PlaybackException(message);

/// <summary>The caller does not own this session and is not an admin.</summary>
public sealed class PlaybackNotOwnerException(Guid sessionId)
    : PlaybackException($"Playback session {sessionId} belongs to another user.");

public sealed class PlaybackTrackNotFoundException(Guid trackId)
    : PlaybackException($"No track {trackId}.");

/// <summary>
/// cosine cannot produce the format this device needs.
/// <para>
/// A device that requires a real <c>Content-Length</c> is no longer a reason to land here - that
/// case is served by materialising the transcode into the existing cache first, so a genuine
/// length exists before the device is told to fetch anything. What remains is the honest
/// failure: ffmpeg has no encoder for the target, or the encode ran and produced nothing.
/// </para>
/// </summary>
public sealed class PlaybackFormatUnavailableException(string format, string? reason = null)
    : PlaybackException(
        $"This device needs {format}, which cosine cannot produce" +
        (reason is null ? "." : $": {reason}") +
        " Set the device's transcode profile to a format it can play directly.")
{
    public string Format { get; } = format;
}
