using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

/// <summary>
/// The offline half of multi-artist support. Splitting artist strings is the kind of thing that
/// looks trivial and then mints an artist called "DC" out of "AC/DC", so the negative cases here
/// carry as much weight as the positive ones: a credit this parser under-reads is exactly today's
/// behaviour, while one it over-reads puts a fictional artist in the library.
/// </summary>
public class ArtistCreditParsingTests
{
    private static string[] Names(string artist, string? artists = null, string? title = null)
        => ArtistCreditParsing.Parse(artist, artists, title).Select(c => c.Name).ToArray();

    // ---- single artist -----------------------------------------------------------------

    [Fact]
    public void PlainArtist_IsOneCredit()
    {
        var credits = ArtistCreditParsing.Parse("Pouya");

        var credit = Assert.Single(credits);
        Assert.Equal("Pouya", credit.Name);
        Assert.Equal("", credit.JoinPhrase);
    }

    /// <summary>
    /// The whole reason this parser never splits on "/", "&amp;" or ",". Each of these is one
    /// artist whose name contains a character a naive splitter would key on.
    /// </summary>
    [Theory]
    [InlineData("AC/DC")]
    [InlineData("Simon & Garfunkel")]
    [InlineData("Tyler, The Creator")]
    [InlineData("Emerson, Lake & Palmer")]
    [InlineData("Earth, Wind & Fire")]
    [InlineData("Florence + the Machine")]
    [InlineData("$uicideboy$")]
    public void ArtistNamesContainingSeparators_AreNotSplit(string name)
    {
        var credit = Assert.Single(ArtistCreditParsing.Parse(name));

        Assert.Equal(name, credit.Name);
    }

    /// <summary>"ft" and "feat" only separate when they stand alone as words.</summary>
    [Theory]
    [InlineData("Ft. Lauderdale Sound")]
    [InlineData("Daft Punk")]
    [InlineData("Featuring")]
    public void FeatureMarkersInsideAName_AreNotSeparators(string name)
        => Assert.Equal([name], Names(name));

    // ---- featuring markers in the ARTIST string ----------------------------------------

    [Theory]
    [InlineData("Skrillex feat. Damian Marley")]
    [InlineData("Skrillex feat Damian Marley")]
    [InlineData("Skrillex ft. Damian Marley")]
    [InlineData("Skrillex ft Damian Marley")]
    [InlineData("Skrillex featuring Damian Marley")]
    [InlineData("Skrillex FEAT. Damian Marley")]
    public void FeaturingMarker_SplitsTheCredit(string artist)
        => Assert.Equal(["Skrillex", "Damian Marley"], Names(artist));

    /// <summary>
    /// The join phrase is the literal text that was there, so the rendered credit is the tag
    /// back verbatim - "feat." and "featuring" are not silently normalised to each other.
    /// </summary>
    [Theory]
    [InlineData("Skrillex feat. Damian Marley")]
    [InlineData("Skrillex featuring Damian Marley")]
    [InlineData("Pouya & Fat Nick")]
    [InlineData("Tyler, The Creator")]
    public void Render_RoundTripsTheOriginalString(string artist)
        => Assert.Equal(artist, ArtistCreditParsing.Render(ArtistCreditParsing.Parse(artist)));

    [Fact]
    public void GuestListBehindAMarker_IsSplitOnAmpersand()
        => Assert.Equal(
            ["Eminem", "Dr. Dre", "50 Cent"],
            Names("Eminem feat. Dr. Dre & 50 Cent"));

    /// <summary>
    /// A comma inside a featuring clause is ambiguous - "Tyler, The Creator" is one artist -
    /// so the clause stays whole. Under-reading beats inventing "The Creator".
    /// </summary>
    [Fact]
    public void GuestListWithACommaOnly_StaysOneCredit()
        => Assert.Equal(["Kanye West", "Tyler, The Creator"], Names("Kanye West feat. Tyler, The Creator"));

    [Theory]
    [InlineData("Skrillex feat.")]
    [InlineData("Skrillex feat. ")]
    public void DanglingMarker_IsNotACredit(string artist)
        => Assert.Equal(["Skrillex"], Names(artist));

    [Fact]
    public void MarkerAtTheStart_IsNotASeparator()
        => Assert.Equal(["feat. Somebody"], Names("feat. Somebody"));

    [Fact]
    public void EmptyArtist_YieldsNoCredits()
        => Assert.Empty(ArtistCreditParsing.Parse("   "));

    // ---- the ARTISTS tag ---------------------------------------------------------------

    [Fact]
    public void ArtistsTag_ProvidesTheNames()
        => Assert.Equal(
            ["Pouya", "Fat Nick"],
            Names("Pouya & Fat Nick", "Pouya\0Fat Nick"));

    [Fact]
    public void ArtistsTag_SemicolonSeparated_IsAlsoAccepted()
        => Assert.Equal(["Pouya", "Fat Nick"], Names("Pouya & Fat Nick", "Pouya; Fat Nick"));

    /// <summary>
    /// ARTISTS carries names but no join phrases; the ARTIST string carries the phrases but
    /// not the boundaries. Lining them up recovers both, so " &amp; " does not become " feat. ".
    /// </summary>
    [Fact]
    public void ArtistsTag_RecoversJoinPhrasesFromTheDisplayString()
    {
        var credits = ArtistCreditParsing.Parse("Pouya feat. Fat Nick", "Pouya\0Fat Nick");

        Assert.Equal(["Pouya", "Fat Nick"], credits.Select(c => c.Name));
        Assert.Equal(" feat. ", credits[0].JoinPhrase);
        Assert.Equal("", credits[1].JoinPhrase);
        Assert.Equal("Pouya feat. Fat Nick", ArtistCreditParsing.Render(credits));
    }

    /// <summary>An ARTIST string the names do not occur in still yields every name.</summary>
    [Fact]
    public void ArtistsTag_ThatDoesNotMatchTheDisplayString_StillYieldsEveryName()
    {
        var credits = ArtistCreditParsing.Parse("Various Artists", "Pouya\0Fat Nick");

        Assert.Equal(["Pouya", "Fat Nick"], credits.Select(c => c.Name));
        Assert.Equal(" & ", credits[0].JoinPhrase);
        Assert.Equal("", credits[1].JoinPhrase);
    }

    /// <summary>
    /// A single-valued ARTISTS tag is what a Picard-tagged solo track has - it must not
    /// suppress the feat. split that the ARTIST string still deserves.
    /// </summary>
    [Fact]
    public void SingleValuedArtistsTag_FallsBackToTheArtistString()
        => Assert.Equal(["Skrillex", "Damian Marley"], Names("Skrillex feat. Damian Marley", "Skrillex"));

    // ---- featuring suffix on the title -------------------------------------------------

    [Theory]
    [InlineData("Run It Up (feat. Fat Nick)")]
    [InlineData("Run It Up [feat. Fat Nick]")]
    [InlineData("Run It Up (ft. Fat Nick)")]
    [InlineData("Run It Up (featuring Fat Nick)")]
    [InlineData("Run It Up (with Fat Nick)")]
    public void FeaturingSuffixInTheTitle_AddsAGuest(string title)
    {
        var credits = ArtistCreditParsing.Parse("Pouya", null, title);

        Assert.Equal(["Pouya", "Fat Nick"], credits.Select(c => c.Name));
        Assert.Equal(" feat. ", credits[0].JoinPhrase);
    }

    /// <summary>
    /// "with" is only a credit inside brackets at the end. Unbracketed it is an ordinary title
    /// word - Pink Floyd's "…Grooving With a Pict" is the case that proves it.
    /// </summary>
    [Theory]
    [InlineData("Several Species of Small Furry Animals Gathered Together in a Cave and Grooving With a Pict")]
    [InlineData("Bury a Friend")]
    [InlineData("Live at Wembley (Remastered)")]
    [InlineData("Houdini")]
    public void OrdinaryTitles_AddNoGuest(string title)
        => Assert.Equal(["Eminem"], Names("Eminem", null, title));

    [Fact]
    public void GuestAlreadyCredited_IsNotDuplicated()
        => Assert.Equal(
            ["Skrillex", "Damian Marley"],
            Names("Skrillex feat. Damian Marley", null, "Make It Bun Dem (feat. Damian Marley)"));

    [Fact]
    public void TitleGuestsAppendToAnExistingCredit()
    {
        var credits = ArtistCreditParsing.Parse("Pouya & Fat Nick", "Pouya\0Fat Nick", "Byself (feat. Germ)");

        Assert.Equal(["Pouya", "Fat Nick", "Germ"], credits.Select(c => c.Name));
        Assert.Equal(", ", credits[1].JoinPhrase);
        Assert.Equal("", credits[2].JoinPhrase);
    }

    /// <summary>The library that prompted all this: every track is a single clean artist.</summary>
    [Fact]
    public void LidarrTaggedTrack_YieldsExactlyThePrimaryArtist()
    {
        var credits = ArtistCreditParsing.Parse("Pouya", "Pouya", "Run It Up");

        Assert.Equal("Pouya", Assert.Single(credits).Name);
    }
}
