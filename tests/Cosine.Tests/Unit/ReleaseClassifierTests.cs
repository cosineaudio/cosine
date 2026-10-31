using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class ReleaseClassifierTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("Single")]
    [InlineData(" SINGLE ")]
    public void IsSingle_SingleTag_IsTrue(string releaseType)
    {
        // Even a multi-track "single" release is added whole.
        Assert.True(ReleaseClassifier.IsSingle(releaseType, trackCount: 3));
    }

    [Theory]
    [InlineData("album")]
    [InlineData("ep")]
    [InlineData("broadcast")]
    [InlineData("other")]
    public void IsSingle_NonSingleTag_IsFalse(string releaseType)
    {
        // A tagged album/EP is never a single, regardless of track count.
        Assert.False(ReleaseClassifier.IsSingle(releaseType, trackCount: 1));
    }

    [Fact]
    public void IsSingle_NoTag_OneTrack_FallsBackToSingle()
    {
        Assert.True(ReleaseClassifier.IsSingle(null, trackCount: 1));
        Assert.True(ReleaseClassifier.IsSingle("", trackCount: 1));
    }

    [Fact]
    public void IsSingle_NoTag_ManyTracks_FallsBackToAlbum()
    {
        Assert.False(ReleaseClassifier.IsSingle(null, trackCount: 8));
    }

    [Fact]
    public void IsSingle_NoTag_RespectsCustomThreshold()
    {
        // With a ≤2 threshold, a 2-track untagged release is a single.
        Assert.True(ReleaseClassifier.IsSingle(null, trackCount: 2, singleThreshold: 2));
        Assert.False(ReleaseClassifier.IsSingle(null, trackCount: 3, singleThreshold: 2));
    }
}
