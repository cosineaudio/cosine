using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cosine.Playback.Abstractions;

namespace Cosine.Playback.Upnp.Didl;

/// <summary>
/// Builds the DIDL-Lite document that goes into <c>SetAVTransportURI</c>'s second argument.
/// <para>
/// It is not optional decoration. Many renderers - Volumio included - show "Unknown" or refuse
/// the track outright without it, and on Sonos nothing appears in the app at all (plan §5
/// Phase 2). The URL alone gets bytes moving on some devices and nothing on others.
/// </para>
/// <para>
/// <strong>Escaping is escaped exactly once, here.</strong> The output is a standalone XML
/// document. It is then handed to <c>ISoapInvoker</c> as an ordinary string argument and escaped
/// a second time by the SOAP layer, which is what turns it into a legal payload inside the
/// envelope. Pre-escaping for SOAP here would produce <c>&amp;amp;amp;</c> on the wire and a
/// renderer that displays literal entity text - so this type escapes for XML and stops.
/// </para>
/// <para>
/// Pure: a record in, a string out. No clock, no network, no device knowledge. Renderer quirks
/// belong in <c>IRendererProfile</c> (plan §1.4), and there are deliberately no conditionals here.
/// </para>
/// </summary>
public static class DidlLiteBuilder
{
    private static readonly XNamespace Didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace Dlna = "urn:schemas-dlna-org:metadata-1-0/";

    /// <summary>
    /// The UPnP class for a single audio track. Renderers branch on this to decide whether the
    /// item is music, a radio stream, a video or a photo, and several use it to choose which
    /// now-playing screen to draw.
    /// </summary>
    public const string MusicTrackClass = "object.item.audioItem.musicTrack";

    /// <summary>
    /// The <c>parentID</c> every item carries.
    /// <para>
    /// cosine exposes no <c>ContentDirectory</c> service - it is a control point that hands over
    /// an HTTP URL, not a media server with a browsable hierarchy. So there is no container this
    /// item is genuinely inside. <c>-1</c> is the conventional "no parent" value and is what
    /// control points have always sent for a synthesised item; <c>0</c> would be a claim that the
    /// item is a child of a ContentDirectory root container that does not exist, which is the
    /// kind of lie a renderer can act on by trying to browse it.
    /// </para>
    /// </summary>
    public const string ParentId = "-1";

    /// <summary>
    /// Shown when a track has no usable title. <c>dc:title</c> is required by DIDL-Lite and an
    /// empty one leaves several renderers with a blank now-playing screen and no fallback.
    /// </summary>
    private const string UnknownTitle = "Unknown Track";

    /// <summary>
    /// Build the metadata document for one track.
    /// </summary>
    /// <param name="track">What the stream resolver produced. Never mutated.</param>
    /// <param name="delivery">
    /// How <see cref="TrackPresentation.Url"/> will be served, which decides the seek and
    /// conversion flags in <c>res@protocolInfo</c>. See <see cref="DlnaDelivery"/>.
    /// </param>
    /// <returns>
    /// A well-formed, standalone XML document with no XML declaration. The declaration is omitted
    /// because this string is destined to be a SOAP argument value: it is legal there but a
    /// fragment parser that meets one after the envelope's own declaration can reject the whole
    /// document, and no server in the wild emits one.
    /// </returns>
    public static string Build(TrackPresentation track, DlnaDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(track);

        var title = Sanitise(track.Title);
        if (title.Length == 0) title = UnknownTitle;

        var artist = Sanitise(track.Artist);
        var album = Sanitise(track.Album);
        var coverArtUrl = Sanitise(track.CoverArtUrl);

        var item = new XElement(
            Didl + "item",
            new XAttribute("id", ItemId(track.TrackId)),
            new XAttribute("parentID", ParentId),
            // "1" rather than "true": both are legal per the schema's xs:boolean, and "1" is what
            // every server emits and therefore the one every hand-rolled parser has been tested
            // against. It means the control point may not modify the item, which is accurate.
            new XAttribute("restricted", "1"),
            new XElement(Dc + "title", title));

        // Renderers disagree about which of these two holds the artist - dc:creator is the Dublin
        // Core original, upnp:artist the UPnP AV addition - and there is no way to know in advance
        // which one a given box reads. Emitting both costs a few bytes and removes the question.
        if (artist.Length > 0)
        {
            item.Add(new XElement(Dc + "creator", artist));
            item.Add(new XElement(Upnp + "artist", artist));
        }

        if (album.Length > 0) item.Add(new XElement(Upnp + "album", album));

        item.Add(new XElement(Upnp + "class", MusicTrackClass));

        if (coverArtUrl.Length > 0) item.Add(new XElement(Upnp + "albumArtURI", coverArtUrl));

        // Optional elements are omitted when blank rather than emitted empty. An absent
        // upnp:artist lets a renderer apply its own "Unknown Artist" fallback; an empty one is a
        // value, so the renderer faithfully displays a blank line where the artist should be.
        // dc:title is the exception above precisely because it is required and cannot be dropped.
        item.Add(BuildResource(track, delivery));

        var root = new XElement(
            Didl + "DIDL-Lite",
            // The default namespace is declared explicitly rather than left to the writer to
            // infer, purely so it comes first: that is the prologue order every DIDL document in
            // the wild has, and the renderers with hand-rolled parsers were written against those
            // documents rather than against the schema.
            new XAttribute("xmlns", Didl.NamespaceName),
            // dc and upnp would be declared anyway by virtue of being used. dlna is declared
            // although nothing below uses it: the canonical DIDL prologue every renderer has been
            // tested against carries all four, and hand-rolled parsers do key off it. cosine has
            // nothing honest to put in that namespace - the cover art endpoint serves whatever
            // the source image is, at whatever size was asked for, so a dlna:profileID of
            // "JPEG_TN" on the albumArtURI would be a guess about somebody else's file.
            new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "upnp", Upnp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dlna", Dlna.NamespaceName),
            item);

        // No indentation: the document travels as a SOAP argument, where every character is
        // escaped a second time, and pretty-printing adds whitespace text nodes inside elements
        // that a lenient parser will happily hand back as part of a title.
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// The DIDL-Lite <c>duration</c> format: <c>H:MM:SS.mmm</c>, hours unpadded, everything else
    /// fixed width. Kept out of <see cref="Build"/> because it is the piece most likely to be
    /// wrong in a way nothing else notices - a renderer that cannot parse the duration usually
    /// shows a zero-length scrubber rather than failing, so it has to be testable on its own.
    /// </summary>
    /// <param name="duration">
    /// Negative is clamped to zero. It should never happen, but emitting <c>-1:00:00.000</c> is
    /// the sort of value that makes a renderer reject the whole item rather than one attribute.
    /// </param>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        // Not "hh" from a standard format string: durations over 24 hours must not wrap into a
        // days component that the format silently drops.
        var hours = (long)duration.TotalHours;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}");
    }

    /// <summary>
    /// The item's <c>id</c>. Derived from the track so it is stable for one track and distinct
    /// between two.
    /// <para>
    /// A constant id - <c>0</c> is the usual choice - is the tempting option and it is the one
    /// that misbehaves: renderers cache now-playing metadata keyed on the object id, so every
    /// track in a queue sharing an id is how a device ends up showing the first track's title for
    /// the whole album. The prefix keeps it from colliding with an id from a real ContentDirectory
    /// the renderer is browsing at the same time, and a GUID contains nothing needing escaping.
    /// </para>
    /// </summary>
    public static string ItemId(Guid trackId) => $"cosine-{trackId:D}";

    private static XElement BuildResource(TrackPresentation track, DlnaDelivery delivery)
    {
        var res = new XElement(
            Didl + "res",
            new XAttribute("protocolInfo", ProtocolInfo.For(track.ContentType, delivery)),
            new XAttribute("duration", FormatDuration(track.Duration)),
            Sanitise(track.Url));

        // Emitted only when there is a real length to emit. A missing size is normal for a live
        // transcode and renderers cope; a size of 0 is a positive claim that the resource is empty,
        // and a renderer that believes it refuses the track without ever opening the URL.
        if (track.ContentLength is > 0)
        {
            res.SetAttributeValue(
                "size", track.ContentLength.Value.ToString(CultureInfo.InvariantCulture));
        }

        return res;
    }

    /// <summary>
    /// Drop characters XML 1.0 cannot represent, then trim.
    /// <para>
    /// Tag text is a real source of these: titles carry stray C0 controls from badly written ID3
    /// frames, and a truncated multi-byte decode leaves a lone surrogate behind. Neither can be
    /// escaped - there is no entity for U+0007 and none for half a code point - so an XML writer
    /// either throws or emits a document no renderer can parse. Stripping loses one invisible
    /// character; the alternative loses the track.
    /// </para>
    /// <para>
    /// Everything XML 1.0 *does* allow is kept, including tab, newline and carriage return.
    /// Escaping the characters that need escaping is the XML writer's job and it is not repeated
    /// here - doing both is how a title ends up displaying <c>&amp;amp;</c>.
    /// </para>
    /// </summary>
    private static string Sanitise(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        if (!NeedsStripping(value)) return value.Trim();

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            // A well-formed surrogate pair is a supplementary-plane code point - an emoji in an
            // album title - which XML 1.0 allows. Taken two at a time so the pair survives the
            // per-char check below, which rejects surrogates individually and would otherwise
            // strip every one of them.
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }

            if (XmlConvert.IsXmlChar(c)) builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static bool NeedsStripping(string value)
    {
        foreach (var c in value)
        {
            if (!XmlConvert.IsXmlChar(c)) return true;
        }

        return false;
    }
}
