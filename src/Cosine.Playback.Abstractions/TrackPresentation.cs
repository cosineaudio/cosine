namespace Cosine.Playback.Abstractions;

/// <summary>
/// A track as presented to a device: everything a driver needs to play it and to display it,
/// and nothing about how cosine stores it.
/// <para>
/// One shape serves both transport modes. A pull driver hands <see cref="Url"/> to the device
/// and the device fetches it; a push driver opens the same URL itself and feeds its encoder.
/// </para>
/// </summary>
/// <param name="TrackId">cosine's track id, for correlating poll results back to the queue.</param>
/// <param name="Url">
/// Absolute, built from <c>Playback:AdvertisedBaseUrl</c> - an address the *speaker* can reach,
/// never one derived from the browser's request host (plan §4.3b). Carries its own bearer
/// credential in the path, so it is not <c>[Authorize]</c>-protected.
/// </param>
/// <param name="ContentType">MIME type of what that URL will actually serve.</param>
/// <param name="ContentLength">
/// Byte length, or null when the response will be chunked. Must not be null for a device whose
/// <see cref="PlaybackCapabilities.RequiresContentLength"/> is true.
/// </param>
/// <param name="Duration">Track duration from the library, used before the device reports one.</param>
/// <param name="CoverArtUrl">Absolute like <see cref="Url"/>, or null when there is no art.</param>
/// <param name="IsConverted">
/// True when cosine re-encoded the audio rather than serving the library file, so the device may
/// not trust the original file's headers.
/// <para>
/// Carried explicitly because it cannot be derived. <see cref="ContentLength"/> already says
/// whether the response is seekable, but seekability and conversion are independent: a transcode
/// materialised to the cache before the device is told about it is seekable <em>and</em>
/// converted. A driver that inferred one from the other would tell the renderer something untrue
/// about a response it is about to fetch - and DLNA's <c>DLNA.ORG_CI</c> flag is exactly this
/// fact, declared separately from <c>DLNA.ORG_OP</c> for the same reason.
/// </para>
/// </param>
public record TrackPresentation(
    Guid TrackId,
    string Url,
    string ContentType,
    long? ContentLength,
    TimeSpan Duration,
    string Title,
    string Artist,
    string Album,
    string? CoverArtUrl,
    bool IsConverted = false);
