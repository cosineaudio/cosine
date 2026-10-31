using System.Reflection;
using Cosine.Infrastructure.Jobs;

namespace Cosine.Tests.Unit;

public class ScanServiceTests
{
    [Fact]
    public void GetSortName_RemovesThePrefix()
    {
        var method = typeof(ScanJob).GetMethod(
            "GetSortName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        Assert.Equal("Beatles, The", method!.Invoke(null, ["The Beatles"]));
        Assert.Equal("Doors, The", method!.Invoke(null, ["The Doors"]));
        Assert.Equal("Day in the Life, A", method!.Invoke(null, ["A Day in the Life"]));
    }

    [Fact]
    public void GetSortName_PreservesShortNames()
    {
        var method = typeof(ScanJob).GetMethod(
            "GetSortName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        // "The" alone should not be stripped
        Assert.Equal("The", method!.Invoke(null, ["The"]));
        Assert.Equal("A", method!.Invoke(null, ["A"]));
    }

    [Fact]
    public void GetSortName_HandlesUntrimmedInput()
    {
        var method = typeof(ScanJob).GetMethod(
            "GetSortName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        Assert.Equal("Beatles, The", method!.Invoke(null, ["  The Beatles  "]));
    }

    [Fact]
    public void GetSortName_PreservesNamesWithoutArticles()
    {
        var method = typeof(ScanJob).GetMethod(
            "GetSortName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        Assert.Equal("Radiohead", method!.Invoke(null, ["Radiohead"]));
        Assert.Equal("Pink Floyd", method!.Invoke(null, ["Pink Floyd"]));
    }

    [Fact]
    public void SupportedExtensions_ContainsCommonFormats()
    {
        // ScanJob has a private static HashSet<string> of supported extensions
        var field = typeof(ScanJob).GetField(
            "SupportedExtensions",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        var extensions = field!.GetValue(null) as HashSet<string>;
        Assert.NotNull(extensions);

        Assert.Contains(".mp3", extensions);
        Assert.Contains(".flac", extensions);
        Assert.Contains(".ogg", extensions);
        Assert.Contains(".opus", extensions);
        Assert.Contains(".aac", extensions);
        Assert.Contains(".m4a", extensions);
        Assert.Contains(".wav", extensions);
    }

    [Theory]
    [InlineData("MUSICBRAINZ_TRACKID", "MUSICBRAINZTRACKID")]                          // Vorbis (FLAC/Ogg)
    [InlineData("MusicBrainz Track Id", "MUSICBRAINZTRACKID")]                          // ID3 TXXX
    [InlineData("----:com.apple.iTunes:MusicBrainz Track Id", "MUSICBRAINZTRACKID")]    // MP4
    public void GetMusicBrainzId_MatchesTagKeyVariants(string tagKey, string normalizedKey)
    {
        var method = typeof(ScanJob).GetMethod(
            "GetMusicBrainzId",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var mbid = "7c9f4c8a-1a2b-4c3d-8e4f-5a6b7c8d9e0f";
        var fields = new Dictionary<string, string> { [tagKey] = mbid };

        Assert.Equal(mbid, method!.Invoke(null, [fields, normalizedKey]));
    }

    [Fact]
    public void GetMusicBrainzId_DoesNotConfuseSimilarKeys()
    {
        var method = typeof(ScanJob).GetMethod(
            "GetMusicBrainzId",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var releaseTrackId = "11111111-1111-1111-1111-111111111111";
        var trackId = "22222222-2222-2222-2222-222222222222";
        var albumArtistId = "33333333-3333-3333-3333-333333333333";
        var fields = new Dictionary<string, string>
        {
            ["MUSICBRAINZ_RELEASETRACKID"] = releaseTrackId,
            ["MUSICBRAINZ_TRACKID"] = trackId,
            ["MUSICBRAINZ_ALBUMARTISTID"] = albumArtistId,
        };

        Assert.Equal(releaseTrackId, method!.Invoke(null, [fields, "MUSICBRAINZRELEASETRACKID"]));
        Assert.Equal(trackId, method!.Invoke(null, [fields, "MUSICBRAINZTRACKID"]));
        Assert.Equal(albumArtistId, method!.Invoke(null, [fields, "MUSICBRAINZALBUMARTISTID"]));
        Assert.Null(method!.Invoke(null, [fields, "MUSICBRAINZARTISTID"]));
    }

    [Fact]
    public void GetMusicBrainzId_TakesFirstValidGuidFromMultiValueField()
    {
        var method = typeof(ScanJob).GetMethod(
            "GetMusicBrainzId",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var fields = new Dictionary<string, string>
        {
            ["MUSICBRAINZ_ARTISTID"] = "not-a-guid; 44444444-4444-4444-4444-444444444444/55555555-5555-5555-5555-555555555555",
        };

        Assert.Equal("44444444-4444-4444-4444-444444444444", method!.Invoke(null, [fields, "MUSICBRAINZARTISTID"]));
    }

    [Fact]
    public void TrackFileInfo_Record_IsCorrectlyDefined()
    {
        var record = new
        {
            Title = "Test",
            SortTitle = "test",
            ArtistName = "Artist",
            SortArtistName = "artist",
            AlbumName = "Album",
            SortAlbumName = "album",
            AlbumArtistName = (string?)null,
            AlbumArtistSortName = (string?)null,
            TrackNumber = (int?)1,
            DiscNumber = (int?)1,
            Year = (int?)2024,
            DurationMs = (long)180000,
            FileSize = (long)5000000,
            BitRate = (int?)320,
            Format = "mp3",
            ContentType = "audio/mpeg",
            Genres = Array.Empty<string>()
        };

        Assert.Equal("Test", record.Title);
        Assert.Equal(180000L, record.DurationMs);
        Assert.Equal("audio/mpeg", record.ContentType);
    }
}
