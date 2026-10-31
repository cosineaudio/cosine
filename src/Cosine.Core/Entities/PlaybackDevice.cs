namespace Cosine.Core.Entities;

/// <summary>
/// A network renderer cosine has seen at least once, and the settings a user has pinned to it.
/// Discovery is ephemeral - the live device list lives in the playback manager - so this row
/// exists only to carry preferences across a rediscovery or a restart.
/// </summary>
public class PlaybackDevice
{
    public Guid Id { get; set; }

    /// <summary>Which driver found it - "airplay", "upnp", and so on.</summary>
    public string DriverId { get; set; } = string.Empty;

    /// <summary>
    /// The driver's stable handle for this device: a UPnP UDN, an mDNS instance name. Identity
    /// has to survive a DHCP renewal, so it is this and not <see cref="LastAddress"/> that pairs
    /// with <see cref="DriverId"/> to form the unique key.
    /// </summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>The name the device advertises. Overwritten whenever it is rediscovered.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>A user-chosen name, which wins over <see cref="DisplayName"/> when set.</summary>
    public string? UserDisplayName { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastAddress { get; set; }

    /// <summary>"flac", "mp3:320", or null to stream the original file untouched.</summary>
    public string? TranscodeProfile { get; set; }

    /// <summary>Ceiling for the volume cosine will send, so a loud device stays tolerable.</summary>
    public int MaxVolumePercent { get; set; } = 100;

    /// <summary>
    /// Milliseconds to shift synced lyrics by in the web player while this device is the output.
    /// A renderer buffers before it makes a sound - a Sonos by a couple of seconds - so the
    /// position cosine reports runs ahead of what the room hears, and lyrics timed off that
    /// position arrive early. Positive delays the lyrics to match the speaker; negative pulls them
    /// forward for a device that somehow reports late. Display only: nothing about the audio,
    /// the stream, or the session's position changes.
    /// </summary>
    public int LyricOffsetMs { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
