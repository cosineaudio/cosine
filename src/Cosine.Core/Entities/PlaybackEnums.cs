using System.Text.Json.Serialization;

namespace Cosine.Core.Entities;

/// <summary>
/// cosine's view of a session, which is deliberately not the device's transport state
/// (<c>Cosine.Playback.Abstractions.PlaybackState</c>). A device can be Stopped for several
/// different reasons and the session needs to tell them apart.
/// </summary>
/// <remarks>
/// Serialised by name. Both halves of remote playback read this over the wire - the REST command
/// responses and the hub's state events - and a bare ordinal would mean the browser mirrors
/// <c>4</c> rather than <c>Hijacked</c>, and would silently change meaning the day a member is
/// inserted. EF still stores it as an integer; this attribute is about JSON only.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PlaybackSessionState>))]
public enum PlaybackSessionState
{
    /// <summary>Session exists, queue may be empty, nothing has played yet.</summary>
    Idle,
    Playing,
    Paused,

    /// <summary>Queue ran out, or the user stopped it. Still owned, still resumable.</summary>
    Stopped,

    /// <summary>
    /// Someone else took the device - its own UI, another app, another cosine session. Polling
    /// stops; cosine does not fight for control.
    /// </summary>
    Hijacked,

    /// <summary>The device stopped answering. The session is kept so it can resume.</summary>
    Offline,
}

/// <summary>
/// Mirrors the browser's repeat modes so a queue can be handed between local and remote
/// playback without translation.
/// </summary>
/// <remarks>Serialised by name, for the reason given on <see cref="PlaybackSessionState"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PlaybackRepeatMode>))]
public enum PlaybackRepeatMode
{
    Off,
    All,
    One,
}
