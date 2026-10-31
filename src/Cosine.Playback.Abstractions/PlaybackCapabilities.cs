namespace Cosine.Playback.Abstractions;

/// <summary>
/// What a connected device can actually do. Declared per target so device-specific behaviour
/// is a value the engine reads, not a pile of conditionals in the transport path (plan §1.4).
/// </summary>
/// <param name="SupportsSeek">False for devices that can only restart a track.</param>
/// <param name="SupportsPreload">
/// True where the device accepts a next-track hint (UPnP <c>SetNextAVTransportURI</c>).
/// <see cref="IPlaybackTarget.PreloadNextAsync"/> is a no-op when false.
/// </param>
/// <param name="SupportsVolume">False where volume is fixed or controlled elsewhere.</param>
/// <param name="RequiresContentLength">
/// Many DLNA renderers reject chunked responses outright. When true the resolver must serve
/// something with a known length - a direct file, or a transcode already materialised to cache.
/// </param>
/// <param name="Mode">Pull (device fetches the URL) or Push (cosine streams the bytes).</param>
/// <param name="Codecs">
/// Content types the device accepts, most-preferred first. Empty means unknown, in which case
/// callers assume mp3 - the one format every renderer handles.
/// </param>
public record PlaybackCapabilities(
    bool SupportsSeek,
    bool SupportsPreload,
    bool SupportsVolume,
    bool RequiresContentLength,
    TransportMode Mode,
    IReadOnlyList<string> Codecs);
