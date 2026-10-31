using Cosine.Infrastructure.Jobs;

namespace Cosine.Tests.Unit;

/// <summary>
/// #38: lyric sidecars are attached to tracks by filename stem, so an album holding both
/// <c>song.flac</c> and <c>song.mp3</c> has two candidates for <c>song.lrc</c>. The old
/// last-write-wins index handed it to whichever the directory enumeration reached last, which
/// changes between scans; the choice now only depends on the paths themselves.
/// </summary>
public class ScanJobLyricStemTests
{
    private static readonly Guid Flac = Guid.NewGuid();
    private static readonly Guid Mp3 = Guid.NewGuid();

    [Fact]
    public void BuildStemIndex_DistinctStems_AreAllIndexed()
    {
        var index = ScanJob.BuildStemIndex(
        [
            ("Artist/Album/one.flac", Flac),
            ("Artist/Album/two.mp3", Mp3),
        ]);

        Assert.Equal(Flac, index["Artist/Album/one"]);
        Assert.Equal(Mp3, index["Artist/Album/two"]);
    }

    [Fact]
    public void BuildStemIndex_CollidingStems_ResolveTheSameWayInEitherOrder()
    {
        var forward = ScanJob.BuildStemIndex(
        [
            ("Artist/Album/song.flac", Flac),
            ("Artist/Album/song.mp3", Mp3),
        ]);
        var reversed = ScanJob.BuildStemIndex(
        [
            ("Artist/Album/song.mp3", Mp3),
            ("Artist/Album/song.flac", Flac),
        ]);

        Assert.Single(forward);
        Assert.Equal(forward["Artist/Album/song"], reversed["Artist/Album/song"]);
        // ".flac" sorts before ".mp3", and the winner is the first path in ordinal order.
        Assert.Equal(Flac, forward["Artist/Album/song"]);
    }

    [Fact]
    public void BuildStemIndex_ThreeWayCollision_PicksTheFirstPathOrdinally()
    {
        var wav = Guid.NewGuid();
        var index = ScanJob.BuildStemIndex(
        [
            ("Artist/Album/song.wav", wav),
            ("Artist/Album/song.mp3", Mp3),
            ("Artist/Album/song.flac", Flac),
        ]);

        Assert.Equal(Flac, index["Artist/Album/song"]);
    }

    [Fact]
    public void BuildStemIndex_SameStemInDifferentDirectories_DoesNotCollide()
    {
        var index = ScanJob.BuildStemIndex(
        [
            ("Artist/AlbumA/song.flac", Flac),
            ("Artist/AlbumB/song.flac", Mp3),
        ]);

        Assert.Equal(2, index.Count);
        Assert.Equal(Flac, index["Artist/AlbumA/song"]);
        Assert.Equal(Mp3, index["Artist/AlbumB/song"]);
    }

    [Fact]
    public void BuildStemIndex_StemLookupIsCaseInsensitive()
    {
        // Sidecars are matched against this index case-insensitively, as the scanner's other
        // path dictionaries are.
        var index = ScanJob.BuildStemIndex([("Artist/Album/Song.flac", Flac)]);

        Assert.Equal(Flac, index["artist/album/song"]);
    }
}
