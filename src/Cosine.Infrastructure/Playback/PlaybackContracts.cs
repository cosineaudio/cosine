using Cosine.Core.Entities;

namespace Cosine.Infrastructure.Playback;

/// <summary>
/// A device as the picker sees it: the persisted row merged with whatever discovery currently
/// knows. Persisted rows render immediately and live discovery fills in around them, so the
/// popover never blocks on an SSDP wait (plan §4.8 item 4).
/// </summary>
/// <param name="Id">Persisted device id. Stable across rediscovery.</param>
/// <param name="DisplayName">The name to show: the admin's chosen one where there is one.</param>
/// <param name="AdvertisedName">
/// What the device calls itself, kept beside <paramref name="DisplayName"/> so the admin page can
/// show what is being overridden - and so clearing an override has something to fall back to on
/// screen without a reload.
/// </param>
/// <param name="Online">Seen by discovery recently. Offline devices are listed, greyed out.</param>
/// <param name="ActiveSessionId">Non-null when the device is busy.</param>
/// <param name="ActiveSessionOwner">
/// Username currently holding the device, so the UI can say who rather than just "busy".
/// </param>
/// <param name="LyricOffsetMs">
/// Carried on the picker's own list, not behind an admin endpoint, because the web player needs
/// it for whichever device it is driving and every listener sees the same speaker latency.
/// </param>
public record PlaybackDeviceInfo(
    Guid Id,
    string DriverId,
    string DeviceKey,
    string DisplayName,
    string AdvertisedName,
    bool Online,
    string? Address,
    string? TranscodeProfile,
    int MaxVolumePercent,
    int LyricOffsetMs,
    bool Enabled,
    DateTimeOffset? LastSeenAt,
    Guid? ActiveSessionId,
    string? ActiveSessionOwner);

/// <summary>
/// What an admin can pin to a device. Every field is optional and null means "leave it alone",
/// so a form that edits one thing does not have to send the rest back.
/// </summary>
/// <param name="DisplayName">
/// The admin's name for the device. Empty clears the override and the advertised name takes over
/// again - which is why this is not simply null-to-clear: null already means "don't touch".
/// </param>
/// <param name="TranscodeProfile">
/// <c>"mp3:320"</c>, <c>"flac"</c>, or empty to send the original file. The format has to be one
/// the transcoder can actually produce; a profile naming anything else is refused rather than
/// stored, because the failure would otherwise surface as a speaker that goes quiet.
/// </param>
/// <param name="MaxVolumePercent">0–100. Applies to a live session immediately, not just the next one.</param>
/// <param name="LyricOffsetMs">
/// How far to shift synced lyrics while this device plays, in milliseconds; positive shows them
/// later. Bounded to ±<see cref="PlaybackDeviceSettings.MaxLyricOffsetMs"/> - a renderer's buffer
/// is seconds, and a value past that is a typo that would leave the lyrics pane looking broken.
/// </param>
/// <param name="Enabled">False hides the device from the picker and refuses new sessions on it.</param>
public record PlaybackDeviceSettings(
    string? DisplayName = null,
    string? TranscodeProfile = null,
    int? MaxVolumePercent = null,
    int? LyricOffsetMs = null,
    bool? Enabled = null)
{
    /// <summary>Widest lyric shift that is a setting rather than a mistake, either direction.</summary>
    public const int MaxLyricOffsetMs = 10_000;
}

/// <summary>
/// The per-session state pushed over the hub, at most once a second. Deliberately excludes the
/// queue: the queue changes rarely and travels as its own event so the client is not re-diffing
/// the whole list every second (plan §4.8 item 3).
/// </summary>
/// <param name="QueueVersion">
/// Bumped on every queue mutation. A client whose version is behind knows to expect (or ask
/// for) a queue event, so the two streams can't drift silently.
/// </param>
/// <param name="ServerTimestamp">
/// When the position was sampled, so the client can interpolate from a known instant rather
/// than from message-arrival time (plan §4.8 item 2).
/// </param>
/// <param name="AutoPlay">
/// Whether the session tops its own queue up as it runs down. Lives on the session rather than in
/// the browser because that is the whole point of it: the speaker keeps playing with no tab open.
/// </param>
public record PlaybackStateSnapshot(
    Guid SessionId,
    Guid DeviceId,
    string DeviceName,
    PlaybackSessionState State,
    int QueueIndex,
    Guid? CurrentTrackId,
    long PositionMs,
    long? DurationMs,
    int VolumePercent,
    PlaybackRepeatMode RepeatMode,
    bool Shuffle,
    int QueueVersion,
    Guid OwnerUserId,
    string OwnerUsername,
    DateTimeOffset ServerTimestamp,
    bool AutoPlay = false);

/// <param name="QueueVersion">Matches the version in the state snapshot that follows it.</param>
public record PlaybackQueueSnapshot(
    Guid SessionId,
    int QueueVersion,
    IReadOnlyList<PlaybackQueueItem> Items);

/// <summary>
/// One queue entry as the browser renders it. Carries the album and artist ids as well as their
/// names because the UI's rows are not captions: cover art is fetched by album id, and the title
/// and artist are links. Without the ids the remote queue would be a visibly poorer version of
/// the local one - grey squares and dead text - which is exactly what plan §1.3 rules out.
/// </summary>
/// <param name="Suffix">Source file format, for the "FLAC · 939 kbps" badge. What the file
/// <em>is</em>, independent of what this device is being sent.</param>
/// <param name="AutoPlay">
/// True for an entry the session queued itself. The queue panel says where the listener's own
/// queue ended and the radio took over, and only the server knows that for a speaker.
/// </param>
/// <param name="Artist">
/// The whole credit as one string - "Pouya &amp; Fat Nick" - matching what <c>/rest</c> puts in
/// a song's <c>artist</c>.
/// </param>
/// <param name="ArtistId">
/// The <em>primary</em> artist. Scalar, and stays scalar: it is what the row's album grouping
/// and every existing link hang off. <see cref="Artists"/> is what makes each name clickable.
/// </param>
/// <param name="Artists">
/// Every credited artist, in credit order. Present for the same reason the ids above are: a
/// remote queue row that names three artists and links to one is the "visibly poorer version
/// of the local one" this record exists to prevent.
/// </param>
public record PlaybackQueueItem(
    int Position,
    Guid TrackId,
    string Title,
    string Artist,
    Guid ArtistId,
    string Album,
    Guid AlbumId,
    long DurationMs,
    string? Suffix,
    int? BitRate,
    bool AutoPlay = false,
    IReadOnlyList<PlaybackQueueArtist>? Artists = null);

/// <summary>One credited artist on a queue entry - enough to render a link and nothing more.</summary>
public record PlaybackQueueArtist(Guid Id, string Name);

/// <summary>
/// The unprotected contents of a stream token. Scoped to a (session, track) pair and carrying
/// the format decision that was made when it was minted, so the bytes served match what the
/// device was told to expect.
/// </summary>
public record PlaybackStreamTicket(
    Guid SessionId,
    Guid TrackId,
    Guid UserId,
    string? Format,
    int? BitRate);

/// <summary>
/// Cover art needs its own anonymous route for the same reason audio does - a renderer showing
/// album art has no cosine credential - but it is a separate protector purpose so a token for
/// one can never be replayed as a token for the other.
/// </summary>
public record PlaybackCoverArtTicket(Guid SessionId, Guid AlbumId);
