using Cosine.Core.Options;
using Cosine.Core.Interfaces;

namespace Cosine.Tests.Unit;

public class OptionsTests
{
    [Fact]
    public void JwtOptions_HasDefaults()
    {
        var options = new JwtOptions
        {
            SigningKey = "test-key"
        };

        Assert.Equal("Cosine", options.Issuer);
        Assert.Equal("Cosine", options.Audience);
        Assert.Equal(1440, options.ExpirationMinutes);
    }

    [Fact]
    public void MediaOptions_SectionName_IsCorrect()
    {
        Assert.Equal("Media", MediaOptions.Section);
    }

    [Fact]
    public void TranscodingOptions_HasDefaultBitrates()
    {
        var options = new TranscodingOptions();

        Assert.True(options.DefaultBitrates.ContainsKey("mp3"));
        Assert.True(options.DefaultBitrates.ContainsKey("opus"));
        Assert.True(options.DefaultBitrates.ContainsKey("aac"));
        Assert.Equal(192, options.DefaultBitrates["mp3"]);
        Assert.Equal(128, options.DefaultBitrates["opus"]);
    }

    [Fact]
    public void TranscodingOptions_DefaultMaxCache_Is10GB()
    {
        var options = new TranscodingOptions();
        Assert.Equal(10L * 1024 * 1024 * 1024, options.MaxCacheBytes);
    }

    [Fact]
    public void JwtOptions_SectionName_IsCorrect()
    {
        Assert.Equal("Jwt", JwtOptions.Section);
    }

    [Fact]
    public void TranscodeFormat_IsRecord()
    {
        var format = new TranscodeFormat("mp3", "mp3", new[] { 128, 192, 320 });

        Assert.Equal("mp3", format.Name);
        Assert.Equal(3, format.BitRates.Count);
    }

    [Fact]
    public void LibraryStats_IsRecord()
    {
        var stats = new LibraryStats(10, 20, 100, 1024, 3600000);

        Assert.Equal(10, stats.ArtistCount);
        Assert.Equal(20, stats.AlbumCount);
        Assert.Equal(100, stats.TrackCount);
    }

    [Fact]
    public void StreamResult_IsRecord()
    {
        using var ms = new MemoryStream();
        var result = new StreamResult(ms, "audio/mpeg", 1024, true);

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(1024, result.ContentLength);
        Assert.True(result.AcceptRanges);
    }

    [Fact]
    public void SearchResults_IsRecord()
    {
        var results = new SearchResults(
            new List<Cosine.Core.Entities.Artist>(),
            new List<Cosine.Core.Entities.Album>(),
            new List<Cosine.Core.Entities.Track>());

        Assert.Empty(results.Artists);
        Assert.Empty(results.Albums);
        Assert.Empty(results.Tracks);
    }
}
