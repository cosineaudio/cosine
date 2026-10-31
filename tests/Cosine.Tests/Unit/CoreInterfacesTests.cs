using Cosine.Core.Interfaces;
using Cosine.Core.Entities;

namespace Cosine.Tests.Unit;

public class CoreInterfacesTests
{
    [Fact]
    public void LibraryStats_Properties()
    {
        var stats = new LibraryStats(100, 50, 500, 1024 * 1024 * 1024, 3600000);

        Assert.Equal(100, stats.ArtistCount);
        Assert.Equal(50, stats.AlbumCount);
        Assert.Equal(500, stats.TrackCount);
        Assert.Equal(1024 * 1024 * 1024, stats.TotalSizeBytes);
        Assert.Equal(3600000, stats.TotalDurationMs);
    }

    [Fact]
    public void StreamResult_WithAllProperties()
    {
        using var stream = new MemoryStream();
        var result = new StreamResult(stream, "audio/flac", 12345, true);

        Assert.Equal("audio/flac", result.ContentType);
        Assert.Equal(12345, result.ContentLength);
        Assert.True(result.AcceptRanges);
    }

    [Fact]
    public void SearchResults_Empty()
    {
        var results = new SearchResults([], [], []);

        Assert.Empty(results.Artists);
        Assert.Empty(results.Albums);
        Assert.Empty(results.Tracks);
    }

    [Fact]
    public void SearchResults_WithData()
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Test" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album" };
        var track = new Track { Id = Guid.NewGuid(), Title = "Song" };

        var results = new SearchResults([artist], [album], [track]);

        Assert.Single(results.Artists);
        Assert.Single(results.Albums);
        Assert.Single(results.Tracks);
    }

    [Fact]
    public void ScanProgress_Properties()
    {
        var progress = new ScanProgress("scanning", 50, 100, "music/file.mp3");

        Assert.Equal("scanning", progress.Phase);
        Assert.Equal(50, progress.Current);
        Assert.Equal(100, progress.Total);
        Assert.Equal("music/file.mp3", progress.CurrentFile);
    }

    [Fact]
    public void ScanStatus_Idle()
    {
        var status = new ScanStatus(null, "idle", null);

        Assert.Null(status.ScanLogId);
        Assert.Equal("idle", status.Status);
        Assert.Null(status.Progress);
    }

    [Fact]
    public void ScanStatus_Running()
    {
        var scanId = Guid.NewGuid();
        var progress = new ScanProgress("discovering", 10, 500, null);
        var status = new ScanStatus(scanId, "running", progress);

        Assert.Equal(scanId, status.ScanLogId);
        Assert.Equal("running", status.Status);
        Assert.NotNull(status.Progress);
        Assert.Equal(10, status.Progress.Current);
    }
}
