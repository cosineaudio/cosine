using System.Xml.Linq;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp.Didl;

namespace Cosine.Tests.Unit;

/// <summary>
/// The DIDL-Lite document and its <c>res@protocolInfo</c> are the difference between a renderer
/// showing the track and a renderer showing "Unknown" or refusing it outright, and the whole job
/// is the exactness of one string. So these assert the string, not a shape.
/// </summary>
public class UpnpDidlTests
{
    private static readonly XNamespace Didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace Dlna = "urn:schemas-dlna-org:metadata-1-0/";

    private static readonly Guid TrackId = Guid.Parse("6f1d0f6e-1a2b-4c3d-8e9f-0a1b2c3d4e5f");

    private const string StreamUrl = "http://192.168.1.10:8080/playback/stream/CfDJ8Token?x=1&y=2";
    private const string CoverArtUrl = "http://192.168.1.10:8080/playback/coverart/CfDJ8Art?size=600";

    private static TrackPresentation Track(
        string title = "1969",
        string artist = "Boards of Canada",
        string album = "Geogaddi",
        string contentType = "audio/flac",
        long? contentLength = 41_235_889,
        string? coverArtUrl = CoverArtUrl,
        TimeSpan? duration = null) =>
        new(
            TrackId,
            StreamUrl,
            contentType,
            contentLength,
            duration ?? new TimeSpan(0, 0, 4, 21, 640),
            title,
            artist,
            album,
            coverArtUrl);

    private static XElement Item(string document) =>
        XDocument.Parse(document).Root!.Element(Didl + "item")
        ?? throw new InvalidOperationException("no item element");

    // ---- the document parses at all ----------------------------------------------------------

    /// <summary>
    /// Every shape the builder has to survive, in one place. Walked by a <c>[Fact]</c> rather than
    /// fed to a <c>[Theory]</c> because a <see cref="TrackPresentation"/> is not xunit-serialisable
    /// and the generated case names would be worse than the assertion message.
    /// </summary>
    private static (string Name, TrackPresentation Track, DlnaDelivery Delivery)[] EveryShape() =>
    [
        ("typical direct", Track(), DlnaDelivery.Direct),
        ("typical transcode", Track(), DlnaDelivery.LiveTranscode),
        ("everything blank or absent",
            Track(title: "", artist: "", album: "", coverArtUrl: null, contentLength: null),
            DlnaDelivery.Direct),
        ("markup in the title", Track(title: "Tom & Jerry <b>\"x\"</b>"), DlnaDelivery.Direct),
        ("control character and a lone surrogate",
            Track(title: "bell\u0007end", artist: "lone\ud800surrogate"), DlnaDelivery.Direct),
        ("zero duration, unknown content type",
            Track(duration: TimeSpan.Zero, contentType: "application/octet-stream"),
            DlnaDelivery.LiveTranscode),
        ("supplementary-plane text and a parameterised content type",
            Track(title: "\U0001F3B5 emoji", contentType: "audio/L16;rate=44100;channels=2"),
            DlnaDelivery.Direct),
    ];

    [Fact]
    public void Build_ProducesParseableXmlForEveryShape()
    {
        foreach (var (name, track, delivery) in EveryShape())
        {
            var document = DidlLiteBuilder.Build(track, delivery);

            var parsed = XDocument.Parse(document);

            Assert.Equal(Didl + "DIDL-Lite", parsed.Root!.Name);
            Assert.True(parsed.Root.Elements(Didl + "item").Count() == 1, name);
        }
    }

    [Fact]
    public void Build_DeclaresTheFourNamespacesRenderersExpect()
    {
        var root = XDocument.Parse(DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct)).Root!;

        var declared = root.Attributes()
            .Where(a => a.IsNamespaceDeclaration)
            .Select(a => a.Value)
            .ToArray();

        Assert.Contains(Didl.NamespaceName, declared);
        Assert.Contains(Dc.NamespaceName, declared);
        Assert.Contains(Upnp.NamespaceName, declared);
        // Nothing in the document uses the dlna namespace; it is declared because the canonical
        // prologue carries it, so the declaration quietly disappearing is a regression.
        Assert.Contains(Dlna.NamespaceName, declared);
    }

    [Fact]
    public void Build_EmitsNoXmlDeclaration()
    {
        // The blob becomes a SOAP string argument. A declaration is legal there and no server in
        // the wild emits one, so cosine does not either.
        Assert.DoesNotContain(
            "<?xml", DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct), StringComparison.Ordinal);
    }

    // ---- escaping ----------------------------------------------------------------------------

    [Fact]
    public void Build_EscapesMarkupInTheTitleExactlyOnce()
    {
        const string title = "Tom & Jerry <b>\"x\"</b>";

        var document = DidlLiteBuilder.Build(Track(title: title), DlnaDelivery.Direct);

        // Escaped once on the wire...
        Assert.Contains("Tom &amp; Jerry &lt;b&gt;", document, StringComparison.Ordinal);
        // ...and not twice. Pre-escaping for the SOAP layer is the invoker's job, and doing it here
        // as well is how a renderer ends up displaying a literal "&amp;".
        Assert.DoesNotContain("&amp;amp;", document, StringComparison.Ordinal);
        // ...and byte-identical after a parse round trip.
        Assert.Equal(title, Item(document).Element(Dc + "title")!.Value);
    }

    [Fact]
    public void Build_EscapesAmpersandsInTheStreamUrl()
    {
        // Stream URLs carry query strings, so this is the everyday case rather than the exotic one.
        var document = DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct);

        Assert.Contains("?x=1&amp;y=2", document, StringComparison.Ordinal);
        Assert.Equal(StreamUrl, Item(document).Element(Didl + "res")!.Value);
    }

    [Fact]
    public void Build_StripsControlCharactersRatherThanEmittingThem()
    {
        var document = DidlLiteBuilder.Build(Track(title: "bell\u0007end"), DlnaDelivery.Direct);

        // There is no entity for U+0007, so an XML writer either throws or emits a document no
        // renderer can parse. Losing one invisible character beats losing the track.
        Assert.DoesNotContain("\u0007", document, StringComparison.Ordinal);
        Assert.Equal("bellend", Item(document).Element(Dc + "title")!.Value);
    }

    [Fact]
    public void Build_StripsLoneSurrogatesButKeepsWellFormedPairs()
    {
        var track = Track(title: "half\ud800broken", artist: "\U0001F3B5 intact");

        var item = Item(DidlLiteBuilder.Build(track, DlnaDelivery.Direct));

        Assert.Equal("halfbroken", item.Element(Dc + "title")!.Value);
        Assert.Equal("\U0001F3B5 intact", item.Element(Upnp + "artist")!.Value);
    }

    // ---- item identity -----------------------------------------------------------------------

    [Fact]
    public void Build_GivesEachTrackItsOwnItemIdAndNoParent()
    {
        var item = Item(DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct));

        Assert.Equal($"cosine-{TrackId:D}", item.Attribute("id")!.Value);
        Assert.Equal("-1", item.Attribute("parentID")!.Value);
        Assert.Equal("1", item.Attribute("restricted")!.Value);
    }

    [Fact]
    public void Build_ItemIdDiffersBetweenTracks()
    {
        // A shared id is how a renderer that caches now-playing metadata by object id ends up
        // showing the first track's title for the whole album.
        var first = Item(DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct)).Attribute("id")!.Value;
        var second = Item(DidlLiteBuilder.Build(
            Track() with { TrackId = Guid.NewGuid() }, DlnaDelivery.Direct)).Attribute("id")!.Value;

        Assert.NotEqual(first, second);
    }

    // ---- metadata elements -------------------------------------------------------------------

    [Fact]
    public void Build_EmitsMusicTrackClass()
    {
        var item = Item(DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct));

        Assert.Equal("object.item.audioItem.musicTrack", item.Element(Upnp + "class")!.Value);
    }

    [Fact]
    public void Build_EmitsBothArtistSpellingsBecauseRenderersDisagree()
    {
        var item = Item(DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct));

        Assert.Equal("Boards of Canada", item.Element(Dc + "creator")!.Value);
        Assert.Equal("Boards of Canada", item.Element(Upnp + "artist")!.Value);
        Assert.Equal("Geogaddi", item.Element(Upnp + "album")!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_FallsBackToAUsableTitleWhenThereIsNone(string? title)
    {
        // dc:title is required by DIDL-Lite, and an empty one leaves a blank now-playing screen
        // with nothing for the renderer to fall back to.
        var item = Item(DidlLiteBuilder.Build(Track(title: title!), DlnaDelivery.Direct));

        Assert.Equal("Unknown Track", item.Element(Dc + "title")!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_OmitsArtistAndAlbumRatherThanEmittingThemEmpty(string? blank)
    {
        // Absent lets the renderer apply its own "Unknown Artist"; empty is a value, so it
        // faithfully draws a blank line where the artist should be.
        var item = Item(DidlLiteBuilder.Build(
            Track(artist: blank!, album: blank!), DlnaDelivery.Direct));

        Assert.Null(item.Element(Dc + "creator"));
        Assert.Null(item.Element(Upnp + "artist"));
        Assert.Null(item.Element(Upnp + "album"));
        // The required element still carries something usable.
        Assert.Equal("1969", item.Element(Dc + "title")!.Value);
    }

    [Fact]
    public void Build_EmitsAlbumArtUriWhenThereIsCoverArt()
    {
        var item = Item(DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct));

        Assert.Equal(CoverArtUrl, item.Element(Upnp + "albumArtURI")!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_OmitsAlbumArtUriWhenThereIsNone(string? coverArtUrl)
    {
        var item = Item(DidlLiteBuilder.Build(Track(coverArtUrl: coverArtUrl), DlnaDelivery.Direct));

        Assert.Null(item.Element(Upnp + "albumArtURI"));
    }

    // ---- res ---------------------------------------------------------------------------------

    [Fact]
    public void Build_EmitsSizeWhenTheLengthIsKnown()
    {
        var res = Item(DidlLiteBuilder.Build(Track(), DlnaDelivery.Direct)).Element(Didl + "res")!;

        Assert.Equal("41235889", res.Attribute("size")!.Value);
        Assert.Equal("0:04:21.640", res.Attribute("duration")!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void Build_OmitsSizeWhenThereIsNoRealLength(long? contentLength)
    {
        // A missing size is normal for a live transcode. A size of 0 is a positive claim that the
        // resource is empty, and a renderer that believes it never opens the URL at all.
        var res = Item(DidlLiteBuilder.Build(
                Track(contentLength: contentLength), DlnaDelivery.LiveTranscode))
            .Element(Didl + "res")!;

        Assert.Null(res.Attribute("size"));
    }

    [Fact]
    public void Build_ResProtocolInfoTracksTheDeliveryMode()
    {
        var direct = Item(DidlLiteBuilder.Build(Track(contentType: "audio/mpeg"), DlnaDelivery.Direct))
            .Element(Didl + "res")!.Attribute("protocolInfo")!.Value;
        var transcoded = Item(DidlLiteBuilder.Build(Track(contentType: "audio/mpeg"), DlnaDelivery.LiveTranscode))
            .Element(Didl + "res")!.Attribute("protocolInfo")!.Value;

        Assert.Equal(ProtocolInfo.For("audio/mpeg", DlnaDelivery.Direct), direct);
        Assert.Equal(ProtocolInfo.For("audio/mpeg", DlnaDelivery.LiveTranscode), transcoded);
        Assert.NotEqual(direct, transcoded);
    }

    // ---- duration ----------------------------------------------------------------------------

    [Theory]
    // zero
    [InlineData(0, 0, 0, 0, "0:00:00.000")]
    // sub-minute
    [InlineData(0, 0, 42, 500, "0:00:42.500")]
    // fractional, including a millisecond value that must keep its leading zeros
    [InlineData(0, 3, 14, 9, "0:03:14.009")]
    [InlineData(0, 4, 21, 640, "0:04:21.640")]
    // over an hour, and over a day - the hours field must not wrap into a days component that a
    // standard format string would then silently drop
    [InlineData(1, 2, 3, 4, "1:02:03.004")]
    [InlineData(100, 2, 3, 4, "100:02:03.004")]
    public void FormatDuration_UsesTheDidlForm(
        int hours, int minutes, int seconds, int milliseconds, string expected)
    {
        Assert.Equal(
            expected,
            DidlLiteBuilder.FormatDuration(new TimeSpan(0, hours, minutes, seconds, milliseconds)));
    }

    [Fact]
    public void FormatDuration_ClampsNegativeToZero()
    {
        Assert.Equal("0:00:00.000", DidlLiteBuilder.FormatDuration(TimeSpan.FromSeconds(-30)));
    }

    // ---- protocolInfo ------------------------------------------------------------------------

    private const string DirectFeatures =
        "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    private const string TranscodedFeatures =
        "DLNA.ORG_OP=00;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    /// <summary>
    /// A transcode already materialised into the cache: seekable, because it is a plain file, and
    /// converted, because ffmpeg made it. This combination is the whole reason delivery is two
    /// independent facts rather than one two-valued mode - and it is not an edge case, it is what
    /// every renderer that requires a <c>Content-Length</c> is served.
    /// </summary>
    private const string CachedTranscodeFeatures =
        "DLNA.ORG_OP=01;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    [Fact]
    public void ContentFeatures_MatchThePlaybackStreamControllerLiterals()
    {
        // DELIBERATE DUPLICATION, ASSERTED ON PURPOSE. The literals above are copied by hand from
        // what Cosine.Api's PlaybackStreamController computes for the contentFeatures.dlna.org
        // response header. Cosine.Playback.Upnp may not reference Cosine.Api - the dependency runs
        // the other way, and Api references this project only to register DI (plan §4.6) - so
        // there is no compiler-enforced link between them and this test is the link. They must
        // agree: a protocolInfo promising byte-seek against a header that refuses it is exactly
        // what makes a renderer issue a seek and then hang on the refusal.
        Assert.Equal(DirectFeatures, ProtocolInfo.ContentFeaturesFor(DlnaDelivery.Direct));
        Assert.Equal(TranscodedFeatures, ProtocolInfo.ContentFeaturesFor(DlnaDelivery.LiveTranscode));
        Assert.Equal(CachedTranscodeFeatures, ProtocolInfo.ContentFeaturesFor(DlnaDelivery.CachedTranscode));

        // Seekability and conversion are independent, so all four combinations are reachable and
        // the two named above are not a complete description of what cosine serves.
        Assert.Equal(
            "DLNA.ORG_OP=00;DLNA.ORG_CI=0;" + ProtocolInfo.Flags,
            ProtocolInfo.ContentFeaturesFor(new DlnaDelivery(ByteSeekable: false, Converted: false)));
    }

    [Theory]
    // The one audio profile every DLNA renderer implements.
    [InlineData("audio/mpeg", "http-get:*:audio/mpeg:DLNA.ORG_PN=MP3;")]
    [InlineData("audio/mp3", "http-get:*:audio/mp3:DLNA.ORG_PN=MP3;")]
    [InlineData("audio/x-mpeg", "http-get:*:audio/x-mpeg:DLNA.ORG_PN=MP3;")]
    // AAC: the container picks the profile, so MP4 and bare ADTS must not collapse together.
    [InlineData("audio/mp4", "http-get:*:audio/mp4:DLNA.ORG_PN=AAC_ISO_320;")]
    [InlineData("audio/m4a", "http-get:*:audio/m4a:DLNA.ORG_PN=AAC_ISO_320;")]
    [InlineData("audio/aac", "http-get:*:audio/aac:DLNA.ORG_PN=AAC_ADTS_320;")]
    // Headerless L16 is the only uncompressed profile DLNA actually defines. Its parameters play
    // no part in choosing the profile but must survive verbatim onto the wire.
    [InlineData("audio/L16;rate=44100;channels=2",
        "http-get:*:audio/L16;rate=44100;channels=2:DLNA.ORG_PN=LPCM;")]
    // RIFF-WAV is not LPCM - a renderer trusting that profile name decodes the 44-byte header as
    // audio - and "WAV_PCM" is a Microsoft extension DLNA never adopted. So no profile name.
    [InlineData("audio/wav", "http-get:*:audio/wav:")]
    [InlineData("audio/x-wav", "http-get:*:audio/x-wav:")]
    // No standard FLAC profile exists. An invented DLNA.ORG_PN=FLAC is a claim a strict renderer
    // can reject where an absent one is merely uninformative, and every renderer cosine targets
    // that plays FLAC at all does so from the MIME type.
    [InlineData("audio/flac", "http-get:*:audio/flac:")]
    [InlineData("audio/x-flac", "http-get:*:audio/x-flac:")]
    // Same reasoning, with less renderer support to lose.
    [InlineData("audio/ogg", "http-get:*:audio/ogg:")]
    [InlineData("audio/opus", "http-get:*:audio/opus:")]
    // Unrecognised: no guess. The transfer flags are still true, because they describe cosine's
    // own endpoint rather than the payload.
    [InlineData("application/octet-stream", "http-get:*:application/octet-stream:")]
    [InlineData("audio/x-ms-wma", "http-get:*:audio/x-ms-wma:")]
    public void For_MapsContentTypesToProfilesAndAlwaysCarriesTheTransferFlags(
        string contentType, string prefix)
    {
        Assert.Equal(prefix + DirectFeatures, ProtocolInfo.For(contentType, DlnaDelivery.Direct));
        Assert.Equal(prefix + TranscodedFeatures, ProtocolInfo.For(contentType, DlnaDelivery.LiveTranscode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Not recognisably type/subtype, and a colon would silently reshape the four-field value into
    // four different fields.
    [InlineData("garbage")]
    [InlineData("http://not-a-mime")]
    public void For_DegradesToTheWildcardWhenTheContentTypeIsUnusable(string? contentType)
    {
        Assert.Equal("http-get:*:*:" + DirectFeatures, ProtocolInfo.For(contentType, DlnaDelivery.Direct));
    }

    [Fact]
    public void For_EchoesTheContentTypeVerbatimSoItMatchesTheHeader()
    {
        // Renderers do compare res@protocolInfo against the Content-Type they get back, and
        // audio/flac against audio/x-flac is a real mismatch to some of them. So the MIME field is
        // whatever the endpoint will send, not a canonicalised form of it.
        Assert.Equal(
            "http-get:*:audio/x-flac:" + DirectFeatures,
            ProtocolInfo.For("  audio/x-flac  ", DlnaDelivery.Direct));
    }

    [Theory]
    [InlineData("audio/mpeg", "MP3")]
    [InlineData("AUDIO/MPEG", "MP3")]
    [InlineData("audio/mpeg; charset=binary", "MP3")]
    [InlineData("audio/flac", null)]
    [InlineData("audio/wav", null)]
    [InlineData(null, null)]
    public void DlnaProfileNameFor_IgnoresCaseAndParameters(string? contentType, string? expected)
    {
        Assert.Equal(expected, ProtocolInfo.DlnaProfileNameFor(contentType));
    }
}
