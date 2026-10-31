namespace Cosine.Playback.Upnp.Didl;

/// <summary>
/// How the bytes behind a <c>res</c> URL will actually be served. Declared by the caller rather
/// than inferred from the MIME type, because the same content type is served every way: a FLAC
/// file streamed off disk is seekable and unconverted, a FLAC out of a live ffmpeg pipe is
/// neither, and the difference is a property of the endpoint's response rather than of the codec.
/// <para>
/// Two <em>independent</em> facts, deliberately not one three-valued mode. DLNA states them
/// separately - <c>DLNA.ORG_OP</c> is about seeking and <c>DLNA.ORG_CI</c> is about conversion -
/// and cosine genuinely produces all the combinations: a transcode materialised into the cache
/// before the device is told about it (which is what a renderer requiring a
/// <c>Content-Length</c> gets) is byte-seekable <em>and</em> converted. Collapsing the pair into
/// "direct or transcoded" cannot express that, and the mistake is silent - the renderer is simply
/// told something untrue about a response it is about to fetch.
/// </para>
/// </summary>
/// <param name="ByteSeekable">
/// The response carries a real length and honours <c>Range</c>. True for a file on disk and for a
/// cached transcode; false only for live, chunked ffmpeg output.
/// </param>
/// <param name="Converted">
/// cosine re-encoded the audio, so the renderer may not trust the original file's headers.
/// Declared so a renderer that refuses converted content is told up front rather than discovering
/// it mid-track.
/// </param>
public readonly record struct DlnaDelivery(bool ByteSeekable, bool Converted)
{
    /// <summary>The original file, streamed off disk.</summary>
    public static DlnaDelivery Direct => new(ByteSeekable: true, Converted: false);

    /// <summary>
    /// A transcode already materialised into the cache, so it is an ordinary seekable file that
    /// happens to have been produced by ffmpeg. The usual case for a renderer that requires a
    /// content length.
    /// </summary>
    public static DlnaDelivery CachedTranscode => new(ByteSeekable: true, Converted: true);

    /// <summary>Live ffmpeg output: chunked, no length, no seeking.</summary>
    public static DlnaDelivery LiveTranscode => new(ByteSeekable: false, Converted: true);
}

/// <summary>
/// Builds the <c>res@protocolInfo</c> value - <c>http-get:*:&lt;mimeType&gt;:&lt;fourth field&gt;</c>
/// - that renderers key off far more than they key off the MIME type alone.
/// <para>
/// The fourth field carries two independent claims and they are decided independently. The
/// <c>DLNA.ORG_PN</c> profile name describes the <em>payload</em>, so it is emitted only when a
/// standard DLNA profile genuinely covers the format. The <c>OP</c>/<c>CI</c>/<c>FLAGS</c> word
/// describes the <em>HTTP transfer</em>, which cosine always knows because cosine is the origin
/// server - so it is always emitted. A format with no DLNA profile therefore still advertises
/// correct seek and conversion semantics instead of degrading to a bare wildcard and throwing
/// that information away.
/// </para>
/// <para>
/// The whole point of getting this right is that a renderer told it may byte-seek will issue a
/// seek, and a renderer whose seek is then refused hangs rather than falling back. The flag
/// strings below therefore have to agree exactly with what
/// <c>PlaybackStreamController</c> puts in the <c>contentFeatures.dlna.org</c> response header.
/// </para>
/// </summary>
public static class ProtocolInfo
{
    /// <summary>
    /// The only transfer protocol cosine advertises. UPnP allows others (<c>rtsp-rtp-udp</c>,
    /// <c>internal</c>); a pull driver hands over an HTTP URL and nothing else.
    /// </summary>
    private const string Protocol = "http-get";

    /// <summary>
    /// "Unspecified" in every field of a protocolInfo. Used for the network field, which is
    /// meaningless for HTTP, and for the MIME field when there is nothing trustworthy to say.
    /// </summary>
    private const string Wildcard = "*";

    /// <summary>
    /// DLNA v1.5, streaming transfer mode, and background-transfer/connection-stall tolerance -
    /// the combination renderers expect for HTTP audio. Constant across every delivery mode: it
    /// describes the transfer, and only <c>OP</c> and <c>CI</c> describe what is being
    /// transferred.
    /// <para>
    /// <strong>Deliberately duplicated</strong> from <c>PlaybackStreamController</c>, which sends
    /// the same word in the <c>contentFeatures.dlna.org</c> response header. This project may not
    /// reference <c>Cosine.Api</c> - the dependency runs the other way - and <c>Cosine.Api</c>
    /// references this project only to register DI (plan §4.6), so neither can hold the other's
    /// constants without breaking one of those rules. The two must not drift: a
    /// <c>protocolInfo</c> promising byte-seek against a header that refuses it is precisely what
    /// makes a renderer hang on the seek it was invited to issue. The unit tests assert both
    /// sides' literals, so changing one fails the other's test.
    /// </para>
    /// </summary>
    public const string Flags = "DLNA.ORG_FLAGS=01700000000000000000000000000000";

    /// <summary>
    /// The full <c>res@protocolInfo</c> value for a stream cosine is about to advertise.
    /// </summary>
    /// <param name="contentType">
    /// Exactly what the stream endpoint will send in its <c>Content-Type</c> header. Echoed
    /// verbatim (trimmed) rather than canonicalised, so the two agree character for character -
    /// renderers do compare them, and <c>audio/flac</c> against <c>audio/x-flac</c> is a real
    /// mismatch to some of them. Null, blank or implausible degrades to <c>*</c>.
    /// </param>
    /// <param name="delivery">How the endpoint will serve it. See <see cref="DlnaDelivery"/>.</param>
    public static string For(string? contentType, DlnaDelivery delivery)
    {
        var mime = MimeField(contentType);
        var profile = DlnaProfileNameFor(contentType);
        var features = ContentFeaturesFor(delivery);

        var fourth = profile is null ? features : $"DLNA.ORG_PN={profile};{features}";

        // Field three is the MIME type and field two is the network, which HTTP has no notion of.
        return $"{Protocol}:{Wildcard}:{mime}:{fourth}";
    }

    /// <summary>
    /// The <c>OP</c>/<c>CI</c>/<c>FLAGS</c> word for a delivery mode. Separate from
    /// <see cref="For"/> so the agreement with the stream endpoint can be asserted on its own,
    /// without a track or a MIME type in the way.
    /// <para>
    /// <c>DLNA.ORG_OP</c> is two digits, time-seek then byte-seek. cosine honours <c>Range</c> and
    /// has no time-seek, so it is <c>01</c> or <c>00</c> and never <c>10</c> or <c>11</c>.
    /// </para>
    /// </summary>
    public static string ContentFeaturesFor(DlnaDelivery delivery) =>
        $"DLNA.ORG_OP={(delivery.ByteSeekable ? "01" : "00")};" +
        $"DLNA.ORG_CI={(delivery.Converted ? "1" : "0")};" +
        Flags;

    /// <summary>
    /// The standard <c>DLNA.ORG_PN</c> profile name for a content type, or null when no standard
    /// profile covers it.
    /// <para>
    /// Null is the honest answer far more often than it looks, and it is a better answer than a
    /// plausible-sounding invention. A renderer that recognises a profile name trusts it and
    /// stops sniffing; a renderer that validates one it recognises against content that does not
    /// match rejects the item outright with a 714 "illegal MIME type". An omitted
    /// <c>DLNA.ORG_PN</c> simply leaves the renderer to sniff, which is what it would do anyway -
    /// so the cost of saying nothing is small and the cost of lying is the track not playing.
    /// </para>
    /// </summary>
    /// <returns>A profile name, or null to emit no <c>DLNA.ORG_PN</c> parameter at all.</returns>
    public static string? DlnaProfileNameFor(string? contentType) => MediaTypeOf(contentType) switch
    {
        // The one audio profile every DLNA renderer implements, and the one cosine can always
        // fall back to by transcoding.
        "audio/mpeg" or "audio/mp3" or "audio/x-mpeg" or "audio/mpeg3" or "audio/x-mpeg-3" => "MP3",

        // AAC in an MP4/M4A container. AAC_ISO_320 is AAC-LC at up to 320 kbps, which covers
        // everything cosine transcodes and all but the most extravagant direct-served files.
        // A higher-bitrate direct AAC technically overruns the profile; renderers are not
        // observed to police it, and the alternative - claiming nothing for the one lossy format
        // besides MP3 that renderers reliably support - costs more than the theoretical mismatch.
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "AAC_ISO_320",

        // Bare AAC is an ADTS stream, a different DLNA profile from the same codec in MP4. The
        // container is what the profile names, so these must not collapse together.
        "audio/aac" or "audio/aacp" or "audio/x-aac" => "AAC_ADTS_320",

        // Raw big-endian PCM. This is the *only* uncompressed profile DLNA actually defines, and
        // it is defined over headerless L16 - see the WAV case below for why that matters.
        "audio/l16" => "LPCM",

        // A RIFF-WAV payload is not LPCM: LPCM is headerless L16, and a renderer that takes the
        // profile name at its word and starts decoding at byte zero plays the 44-byte RIFF header
        // as audio. There is no standard PN for RIFF-WAV - "WAV_PCM" is a Microsoft extension the
        // DLNA guidelines never adopted - so cosine says nothing and lets the renderer sniff,
        // which it does correctly because RIFF is trivially recognisable.
        "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => null,

        // DLNA never standardised a FLAC profile. Some renderers accept an invented
        // DLNA.ORG_PN=FLAC and a few are documented as wanting it, but a non-standard PN is a
        // claim a strict renderer can reject where an absent one is merely uninformative - and
        // every renderer cosine targets that plays FLAC at all (Sonos, WiiM, BluOS, upmpdcli)
        // does so from the MIME type. Omitting it is the choice that cannot make things worse.
        "audio/flac" or "audio/x-flac" => null,

        // Same as FLAC, with less renderer support to lose. No DLNA profile exists and nothing
        // recognises an invented one.
        "audio/ogg" or "audio/x-ogg" or "audio/vorbis" or "audio/x-vorbis+ogg"
            or "audio/opus" or "audio/x-opus+ogg" => null,

        // Unrecognised. Reached only if the stream endpoint learns a format this table has not,
        // which is exactly when guessing would be worst.
        _ => null,
    };

    /// <summary>
    /// The MIME field of the protocolInfo: the caller's content type, trimmed, or the wildcard.
    /// </summary>
    private static string MimeField(string? contentType)
    {
        var value = contentType?.Trim();
        if (string.IsNullOrEmpty(value)) return Wildcard;

        // protocolInfo is colon-delimited, so a colon in the MIME field silently reshapes the
        // whole value into four different fields. Anything that is not recognisably type/subtype
        // degrades to the wildcard rather than corrupting the ones either side of it.
        if (value.Contains(':') || !value.Contains('/')) return Wildcard;

        return value;
    }

    /// <summary>
    /// The lookup key for <see cref="DlnaProfileNameFor"/>: the media type with any parameters
    /// dropped, lowercased. <c>audio/L16;rate=44100;channels=2</c> carries required parameters
    /// on the wire but they play no part in choosing a profile.
    /// </summary>
    private static string MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var value = contentType.AsSpan();
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0) value = value[..semicolon];

        return value.Trim().ToString().ToLowerInvariant();
    }
}
