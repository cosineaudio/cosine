using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class MetricsServiceTests
{
    [Fact]
    public void CacheCounters_TrackHitsMissesAndRate()
    {
        var metrics = new MetricsService();
        metrics.RecordCacheHit();
        metrics.RecordCacheHit();
        metrics.RecordCacheHit();
        metrics.RecordCacheMiss();

        var snap = metrics.GetLiveSnapshot();

        Assert.Equal(3, snap.CacheHits);
        Assert.Equal(1, snap.CacheMisses);
        Assert.Equal(0.75, snap.CacheHitRate, 3);
    }

    [Fact]
    public void StreamCounters_SplitTranscodeDirectAndCacheHits()
    {
        var metrics = new MetricsService();
        metrics.RecordStream(transcoded: true, transcodeFromCache: true);
        metrics.RecordStream(transcoded: true, transcodeFromCache: false);
        metrics.RecordStream(transcoded: false, transcodeFromCache: false);
        metrics.RecordStream(transcoded: false, transcodeFromCache: false);

        var snap = metrics.GetLiveSnapshot();

        Assert.Equal(4, snap.StreamRequests);
        Assert.Equal(2, snap.TranscodeCount);
        Assert.Equal(1, snap.TranscodeCacheHits);
        Assert.Equal(2, snap.DirectPlayCount);
        Assert.Equal(0.5, snap.TranscodePercent, 3);
        Assert.Equal(0.5, snap.TranscodeCacheHitRate, 3);
    }

    [Fact]
    public void Ttfb_ComputesPercentiles()
    {
        var metrics = new MetricsService();
        for (var i = 1; i <= 100; i++)
            metrics.RecordTtfb(i);

        var snap = metrics.GetLiveSnapshot();

        Assert.Equal(100, snap.TtfbSampleCount);
        Assert.Equal(50, snap.TtfbP50Ms, 0);
        Assert.Equal(95, snap.TtfbP95Ms, 0);
    }

    [Fact]
    public void Ttfb_IgnoresNegativeAndNaN()
    {
        var metrics = new MetricsService();
        metrics.RecordTtfb(-5);
        metrics.RecordTtfb(double.NaN);

        var snap = metrics.GetLiveSnapshot();

        Assert.Equal(0, snap.TtfbSampleCount);
    }

    [Fact]
    public void CaptureInterval_ReturnsDeltasSinceLastCall()
    {
        var metrics = new MetricsService();
        metrics.RecordCacheHit();
        metrics.RecordCacheHit();
        metrics.RecordStream(transcoded: true, transcodeFromCache: false);

        var first = metrics.CaptureInterval();
        Assert.Equal(2, first.CacheHits);
        Assert.Equal(1, first.StreamRequests);
        Assert.Equal(1, first.TranscodeCount);

        // Nothing new since the last capture.
        var second = metrics.CaptureInterval();
        Assert.Equal(0, second.CacheHits);
        Assert.Equal(0, second.StreamRequests);

        metrics.RecordCacheMiss();
        var third = metrics.CaptureInterval();
        Assert.Equal(0, third.CacheHits);
        Assert.Equal(1, third.CacheMisses);
    }

    [Fact]
    public void EmptySnapshot_HasZeroRatesNotNaN()
    {
        var snap = new MetricsService().GetLiveSnapshot();

        Assert.Equal(0, snap.CacheHitRate);
        Assert.Equal(0, snap.TranscodePercent);
        Assert.Equal(0, snap.TranscodeCacheHitRate);
        Assert.Equal(0, snap.TtfbSampleCount);
    }
}
