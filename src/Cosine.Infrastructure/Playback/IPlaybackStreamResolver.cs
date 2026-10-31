using Cosine.Playback.Abstractions;

namespace Cosine.Infrastructure.Playback;

/// <summary>
/// Turns a library track into something a device can fetch: an absolute URL the speaker can
/// reach, a format the device accepts, and a signed token that lets it through without a
/// cosine credential (plan §4.3).
/// </summary>
public interface IPlaybackStreamResolver
{
    /// <summary>
    /// False when <c>Playback:AdvertisedBaseUrl</c> is unset. Callers check this before
    /// starting a session so the failure is one clear message rather than a device that
    /// appears to work and plays silence.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Build the presentation for one track on one device, minting its stream token.
    /// </summary>
    /// <param name="capabilities">
    /// Drives the format choice: a device that requires a content length must not be handed a
    /// chunked live transcode, and an empty codec list means mp3.
    /// </param>
    /// <param name="transcodeProfile">
    /// Per-device override from the device row - <c>"flac"</c>, <c>"mp3:320"</c>, or null to
    /// let capabilities decide.
    /// </param>
    /// <exception cref="PlaybackNotConfiguredException">No advertised base URL.</exception>
    Task<TrackPresentation> ResolveAsync(
        Guid sessionId,
        Guid trackId,
        Guid userId,
        PlaybackCapabilities capabilities,
        string? transcodeProfile,
        CancellationToken ct);

    /// <summary>
    /// Unprotect and validate a token from the stream URL. False for anything tampered with,
    /// malformed, or past its expiry - the caller turns that into a 404, never a 401, so a
    /// probe cannot tell an expired token from one that never existed.
    /// </summary>
    bool TryValidateToken(string token, out PlaybackStreamTicket ticket);

    /// <summary>
    /// As <see cref="TryValidateToken"/>, for the cover-art route. A separate protector purpose,
    /// so neither kind of token is usable on the other's endpoint.
    /// </summary>
    bool TryValidateCoverArtToken(string token, out PlaybackCoverArtTicket ticket);
}
