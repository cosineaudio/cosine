using Cosine.Core.Entities;
using Cosine.Core.Interfaces;

namespace Cosine.Tests.Unit;

public class MediaLibraryServiceTests
{
    [Fact]
    public void GetRandomTracksAsync_InterfaceExists()
    {
        var method = typeof(IMediaLibraryService).GetMethod("GetRandomTracksAsync");
        Assert.NotNull(method);
        Assert.Equal(2, method!.GetParameters().Length); // limit, ct
    }

    [Fact]
    public void GetRandomTracksAsync_DefaultLimit_Is20()
    {
        var method = typeof(IMediaLibraryService).GetMethod("GetRandomTracksAsync");
        var param = method!.GetParameters()[0];
        Assert.True(param.HasDefaultValue);
        Assert.Equal(20, param.DefaultValue);
    }

    [Fact]
    public void StreamResult_NoAcceptRanges_HasCorrectDefaults()
    {
        using var ms = new MemoryStream([5, 6, 7, 8]);
        var result = new StreamResult(ms, "audio/ogg", 4, false);

        Assert.False(result.AcceptRanges);
        Assert.Equal("audio/ogg", result.ContentType);
    }

    [Fact]
    public void LibraryStats_AllFields_MappedCorrectly()
    {
        var stats = new LibraryStats(42, 128, 1024, 10737418240, 86400000);

        Assert.Equal(42, stats.ArtistCount);
        Assert.Equal(128, stats.AlbumCount);
        Assert.Equal(1024, stats.TrackCount);
        Assert.Equal(10737418240, stats.TotalSizeBytes);
        Assert.Equal(86400000, stats.TotalDurationMs);
    }

    [Fact]
    public void LibraryStats_EmptyLibrary()
    {
        var stats = new LibraryStats(0, 0, 0, 0, 0);

        Assert.Equal(0, stats.ArtistCount);
        Assert.Equal(0, stats.AlbumCount);
        Assert.Equal(0, stats.TrackCount);
        Assert.Equal(0, stats.TotalSizeBytes);
        Assert.Equal(0, stats.TotalDurationMs);
    }

    [Fact]
    public void MediaLibraryService_ImplementsAllMethods()
    {
        var methods = typeof(IMediaLibraryService).GetMethods()
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToHashSet();

        Assert.Contains("GetArtistsAsync", methods);
        Assert.Contains("GetArtistAsync", methods);
        Assert.Contains("GetAlbumsAsync", methods);
        Assert.Contains("GetAlbumAsync", methods);
        Assert.Contains("GetTracksAsync", methods);
        Assert.Contains("GetTrackAsync", methods);
        Assert.Contains("GetGenresAsync", methods);
        Assert.Contains("GetRecentlyAddedAsync", methods);
        Assert.Contains("GetRandomAsync", methods);
        Assert.Contains("GetRandomTracksAsync", methods);
        Assert.Contains("GetLibraryStatsAsync", methods);
    }

    [Fact]
    public void Track_WithAllFields_HasCorrectValues()
    {
        var now = DateTimeOffset.UtcNow;
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = "Bohemian Rhapsody",
            SortTitle = "bohemian rhapsody",
            AlbumId = Guid.NewGuid(),
            ArtistId = Guid.NewGuid(),
            TrackNumber = 11,
            DiscNumber = 1,
            DurationMs = 354000,
            FilePath = "queen/night_at_the_opera/11_bohemian_rhapsody.flac",
            FileSize = 32000000,
            BitRate = 1411,
            Format = "flac",
            ContentType = "audio/flac",
            Year = 1975,
            CreatedAt = now,
            UpdatedAt = now
        };

        Assert.Equal("Bohemian Rhapsody", track.Title);
        Assert.Equal(1975, track.Year);
        Assert.Equal(1411, track.BitRate);
        Assert.Equal("flac", track.Format);
        Assert.Equal(354000L, track.DurationMs);
    }

    [Fact]
    public void Album_WithTracks_HasDenormalizedCounts()
    {
        var album = new Album
        {
            Id = Guid.NewGuid(),
            Name = "Dark Side of the Moon",
            SortName = "dark side of the moon",
            TrackCount = 10,
            DurationMs = 2560000
        };

        Assert.Equal(10, album.TrackCount);
        Assert.Equal(2560000L, album.DurationMs);
    }

    [Fact]
    public void Artist_HasIndependentSortName()
    {
        var artist = new Artist
        {
            Name = "Pink Floyd",
            SortName = "pink floyd",
            StarCount = 42
        };

        Assert.Equal("Pink Floyd", artist.Name);
        Assert.Equal("pink floyd", artist.SortName);
        Assert.Equal(42, artist.StarCount);
    }
}
