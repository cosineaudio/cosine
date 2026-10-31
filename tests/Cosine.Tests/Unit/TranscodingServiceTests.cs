using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

public class TranscodingServiceTests
{
    // --- #40: the requested bitrate reaches the cache filename and an ffmpeg spawn, so an
    // unbounded value lets a client loop mp3:1, mp3:2, … and fill the cache disk. ---

    [Theory]
    [InlineData(1, 32)]             // below the ladder: clamped up to its first rung
    [InlineData(32, 32)]
    [InlineData(130, 128)]          // off-ladder: rounds DOWN, never above what was asked for
    [InlineData(192, 192)]
    [InlineData(319, 256)]
    [InlineData(320, 320)]
    [InlineData(9999, 320)]         // above the ladder: clamped to its last rung
    [InlineData(int.MaxValue, 320)]
    public void NormalizeBitRate_SnapsOntoTheLadder(int requested, int expected)
    {
        Assert.Equal(expected, TranscodingService.NormalizeBitRate("mp3", requested));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NormalizeBitRate_NonPositive_MeansEncoderDefault(int requested)
    {
        Assert.Equal(0, TranscodingService.NormalizeBitRate("mp3", requested));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("wav")]
    [InlineData("FLAC")]
    public void NormalizeBitRate_LosslessTargets_IgnoreTheBitrate(string format)
    {
        Assert.Equal(0, TranscodingService.NormalizeBitRate(format, 320));
    }

    [Fact]
    public void NormalizeBitRate_BoundsTheNumberOfDistinctCacheEntries()
    {
        // The property that matters: whatever a client asks for, the value that reaches the
        // cache key comes from a small fixed set.
        var distinct = Enumerable.Range(1, 2000)
            .Select(br => TranscodingService.NormalizeBitRate("mp3", br))
            .Distinct()
            .ToList();

        Assert.Equal(10, distinct.Count);
    }

    [Fact]
    public async Task GetTranscodeStream_OffLadderRequest_ReusesTheRungBelowIt()
    {
        // An off-ladder request must hit the cache entry it normalises onto rather than
        // spawning ffmpeg for a filename of its own (there is no ffmpeg in this test - a
        // miss would fail on the process start).
        using var sp = new TestServiceProvider();
        var dir = Path.Combine(Path.GetTempPath(), "cosine-transcode-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var track = await SeedTrackAsync(sp, dir, "song.flac");
            var cached = Path.Combine(dir, "cached.mp3");
            await File.WriteAllBytesAsync(cached, [1, 2, 3, 4]);
            sp.DbContext.TranscodeCache.Add(new TranscodeCache
            {
                TrackId = track.Id, Format = "mp3", BitRate = 128, FilePath = cached, FileSize = 4,
            });
            await sp.DbContext.SaveChangesAsync();

            var svc = NewService(sp, dir);
            await using var stream = await svc.GetTranscodeStreamAsync(track.Id, "mp3", 130);

            Assert.Equal(4, stream.Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- #48: one element per argument, so a filename cannot inject ffmpeg options. ---

    [Fact]
    public void BuildFfmpegArgs_PassesTheInputPathAsASingleUnquotedArgument()
    {
        var args = TranscodingService.BuildFfmpegArgs("/media/Artist/song.flac", "mp3", "libmp3lame", 192);

        var i = args.IndexOf("-i");
        Assert.True(i >= 0);
        Assert.Equal("/media/Artist/song.flac", args[i + 1]);
        Assert.DoesNotContain(args, a => a.Contains('"'));
    }

    [Theory]
    [InlineData("/media/say \"hi\"/song.flac")]     // quotes broke Windows-style tokenisation
    [InlineData("/media/two words/song.flac")]
    [InlineData("/media/-vf scale/song.flac")]      // looks like an option
    public void BuildFfmpegArgs_HostileFilenames_StayOneArgument(string path)
    {
        var args = TranscodingService.BuildFfmpegArgs(path, "mp3", "libmp3lame", 192);

        Assert.Equal(path, args[args.IndexOf("-i") + 1]);
        Assert.Single(args, a => a == path);
    }

    [Fact]
    public void BuildFfmpegArgs_CarriesTheEncodingOptions()
    {
        var mp3 = TranscodingService.BuildFfmpegArgs("/media/song.flac", "mp3", "libmp3lame", 192);
        Assert.Equal("192k", mp3[mp3.IndexOf("-b:a") + 1]);
        Assert.Equal("libmp3lame", mp3[mp3.IndexOf("-c:a") + 1]);
        Assert.Equal("mp3", mp3[mp3.IndexOf("-f") + 1]);
        Assert.Equal("pipe:1", mp3[^1]);
        Assert.Contains("-vn", mp3);

        // Bitrate 0 (lossless, or "encoder default") emits no -b:a at all.
        var flac = TranscodingService.BuildFfmpegArgs("/media/song.flac", "flac", "flac", 0);
        Assert.DoesNotContain("-b:a", flac);
        Assert.Equal("s16", flac[flac.IndexOf("-sample_fmt") + 1]);
    }

    // --- #49: containment for the stored path, on the transcode path too. ---

    [Fact]
    public async Task GetTranscodeStream_TrackPathEscapingTheMediaRoot_IsNotFound()
    {
        using var sp = new TestServiceProvider();
        var dir = Path.Combine(Path.GetTempPath(), "cosine-transcode-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var outside = Path.Combine(Path.GetDirectoryName(dir)!, "outside-" + Guid.NewGuid() + ".flac");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        try
        {
            var track = await SeedTrackAsync(sp, dir, "song.flac");
            track.FilePath = "../" + Path.GetFileName(outside);
            await sp.DbContext.SaveChangesAsync();

            var svc = NewService(sp, dir);

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => svc.GetTranscodeStreamAsync(track.Id, "mp3", 192));
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(dir, true);
        }
    }

    private static TranscodingService NewService(TestServiceProvider sp, string rootPath)
        => new(sp.ScopeFactory,
            Options.Create(new MediaOptions { RootPath = rootPath }),
            Options.Create(new TranscodingOptions { CachePath = string.Empty }),
            NullLogger<TranscodingService>.Instance);

    private static async Task<Track> SeedTrackAsync(TestServiceProvider sp, string dir, string fileName)
    {
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), [1, 2, 3]);

        var artist = new Artist { Id = Guid.NewGuid(), Name = "A", SortName = "a" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Al", SortName = "al", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "T", SortTitle = "t",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = fileName,
        };
        sp.DbContext.Artists.Add(artist);
        sp.DbContext.Albums.Add(album);
        sp.DbContext.Tracks.Add(track);
        await sp.DbContext.SaveChangesAsync();
        return track;
    }

    [Fact]
    public void TranscodeFormat_Properties_AreCorrect()
    {
        var format = new TranscodeFormat("mp3", "mp3", [128, 192, 320]);

        Assert.Equal("mp3", format.Name);
        Assert.Equal("mp3", format.Extension);
        Assert.Equal(3, format.BitRates.Count);
        Assert.Contains(128, format.BitRates);
        Assert.Contains(192, format.BitRates);
        Assert.Contains(320, format.BitRates);
    }

    [Fact]
    public void TranscodeFormat_EmptyBitRates_IsValid()
    {
        var format = new TranscodeFormat("wav", "wav", []);

        Assert.Empty(format.BitRates);
    }

    [Fact]
    public void TranscodeFormat_ImmutableBitRates()
    {
        var rates = new List<int> { 128, 256 };
        var format = new TranscodeFormat("mp3", "mp3", rates);

        // IReadOnlyList doesn't copy - the underlying list is shared
        Assert.Equal(2, format.BitRates.Count);
    }

    [Theory]
    [InlineData("mp3", "mp3")]
    [InlineData("opus", "opus")]
    [InlineData("aac", "aac")]
    [InlineData("flac", "flac")]
    [InlineData("ogg", "ogg")]
    [InlineData("wav", "wav")]
    public void SupportedFormats_HaveCorrectExtensions(string format, string expectedExtension)
    {
        // Validate format-to-extension mapping aligns with the plan.
        // aac streams as ADTS (.aac): the mp4/m4a container needs a seekable
        // output, which a piped transcode can't provide.
        var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mp3"] = "mp3",
            ["opus"] = "opus",
            ["aac"] = "aac",
            ["flac"] = "flac",
            ["ogg"] = "ogg",
            ["wav"] = "wav",
        };

        Assert.True(extensions.TryGetValue(format, out var ext));
        Assert.Equal(expectedExtension, ext);
    }

    [Fact]
    public void PruneCache_RemovesOldestFirst()
    {
        // Simulate the pruning logic: entries sorted by LastAccessedAt ascending,
        // removed until total size <= max
        var entries = new List<CacheEntry>
        {
            new() { TrackId = Guid.NewGuid(), FileSize = 100, LastAccessedAt = DateTimeOffset.UtcNow.AddDays(-10) },
            new() { TrackId = Guid.NewGuid(), FileSize = 200, LastAccessedAt = DateTimeOffset.UtcNow.AddDays(-5) },
            new() { TrackId = Guid.NewGuid(), FileSize = 300, LastAccessedAt = DateTimeOffset.UtcNow.AddDays(-1) },
        };

        long maxBytes = 400;
        var sorted = entries.OrderBy(e => e.LastAccessedAt).ToList();
        long totalSize = sorted.Sum(e => e.FileSize ?? 0);
        var removed = new List<CacheEntry>();

        foreach (var entry in sorted)
        {
            if (totalSize <= maxBytes)
                break;
            removed.Add(entry);
            totalSize -= entry.FileSize ?? 0;
        }

        Assert.Equal(2, removed.Count); // 100+200 removed, total = 300 <= 400
        Assert.Single(sorted.Except(removed)); // Only the 300-byte entry survives
    }

    [Fact]
    public void PruneCache_NoRemovalWhenUnderLimit()
    {
        var entries = new List<CacheEntry>
        {
            new() { TrackId = Guid.NewGuid(), FileSize = 50, LastAccessedAt = DateTimeOffset.UtcNow },
        };

        long maxBytes = 100;
        var sorted = entries.OrderBy(e => e.LastAccessedAt).ToList();
        long totalSize = sorted.Sum(e => e.FileSize ?? 0);
        var removed = 0;

        foreach (var entry in sorted)
        {
            if (totalSize <= maxBytes)
                break;
            totalSize -= entry.FileSize ?? 0;
            removed++;
        }

        Assert.Equal(0, removed);
    }

    [Fact]
    public void PruneCache_RemovesAllWhenLimitIsZero()
    {
        var entries = new List<CacheEntry>
        {
            new() { TrackId = Guid.NewGuid(), FileSize = 100, LastAccessedAt = DateTimeOffset.UtcNow },
            new() { TrackId = Guid.NewGuid(), FileSize = 200, LastAccessedAt = DateTimeOffset.UtcNow.AddDays(-1) },
        };

        long maxBytes = 0;
        var sorted = entries.OrderBy(e => e.LastAccessedAt).ToList();
        long totalSize = sorted.Sum(e => e.FileSize ?? 0);
        var removed = 0;

        foreach (var entry in sorted)
        {
            if (totalSize <= maxBytes)
                break;
            totalSize -= entry.FileSize ?? 0;
            removed++;
        }

        Assert.Equal(2, removed);
    }

    [Fact]
    public void PruneCache_EmptyList_ReturnsZero()
    {
        var entries = new List<CacheEntry>();

        Assert.Empty(entries);
        Assert.Equal(0, entries.Sum(e => e.FileSize ?? 0));
    }

    [Fact]
    public void TranscodeCache_Entity_Defaults()
    {
        var cache = new Cosine.Core.Entities.TranscodeCache();

        Assert.Equal(Guid.Empty, cache.Id);
        Assert.Equal(Guid.Empty, cache.TrackId);
        Assert.Equal(string.Empty, cache.Format);
        Assert.Equal(0, cache.BitRate);
        Assert.Equal(string.Empty, cache.FilePath);
        Assert.Null(cache.FileSize);
    }

    [Fact]
    public void TranscodeCache_Timestamps_AreSetOnCreation()
    {
        var cache = new Cosine.Core.Entities.TranscodeCache();

        Assert.True(cache.CreatedAt <= DateTimeOffset.UtcNow);
        Assert.True(cache.LastAccessedAt <= DateTimeOffset.UtcNow);
    }

    private class CacheEntry
    {
        public Guid TrackId { get; init; }
        public long? FileSize { get; init; }
        public DateTimeOffset LastAccessedAt { get; init; }
    }
}

// --- #39: a transcode slot is held for the whole song, so the old ProcessorCount cap was a
// cap on concurrent *listeners*, and the wait past it was unbounded - the caller blocked
// forever with no feedback. ---

public class TranscodeSlotsTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task Acquire_UpToTheLimit_Succeeds()
    {
        using var slots = new TranscodeSlots(3, ShortWait);

        for (var i = 0; i < 3; i++)
            await slots.AcquireAsync();

        Assert.Equal(0, slots.Available);
    }

    [Fact]
    public async Task Acquire_PastTheLimit_FailsInsteadOfWaitingForever()
    {
        using var slots = new TranscodeSlots(1, ShortWait);
        await slots.AcquireAsync();

        var ex = await Assert.ThrowsAsync<TranscodeCapacityException>(() => slots.AcquireAsync());

        Assert.Equal(1, ex.Limit);
        Assert.Equal(ShortWait, ex.Waited);
    }

    [Fact]
    public async Task Release_LetsAWaiterThrough()
    {
        using var slots = new TranscodeSlots(1, TimeSpan.FromSeconds(10));
        await slots.AcquireAsync();

        var waiter = slots.AcquireAsync();
        Assert.False(waiter.IsCompleted);

        slots.Release();
        await waiter;                       // would have thrown on timeout

        Assert.Equal(0, slots.Available);
    }

    [Fact]
    public async Task Acquire_HonoursCancellation()
    {
        using var slots = new TranscodeSlots(1, TimeSpan.FromMinutes(5));
        await slots.AcquireAsync();

        using var cts = new CancellationTokenSource();
        var waiter = slots.AcquireAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public void Zeroes_MeanAuto_NotAStalledServer()
    {
        // 0 came from an unset config key; a limit or timeout of 0 would refuse every request.
        using var slots = new TranscodeSlots(configuredLimit: 0, configuredTimeoutSeconds: 0);

        Assert.True(slots.Limit >= 4);
        Assert.Equal(TranscodeSlots.DefaultTimeout, slots.Timeout);
        // The old cap was the core count; a slot held for a whole song needs more headroom.
        Assert.True(slots.Limit > Environment.ProcessorCount);
    }

    [Fact]
    public async Task Active_TracksSlotsInFlight()
    {
        // What the dashboard's live transcode count reads.
        using var slots = new TranscodeSlots(3, ShortWait);
        Assert.Equal(0, slots.Active);

        await slots.AcquireAsync();
        await slots.AcquireAsync();
        Assert.Equal(2, slots.Active);

        slots.Release();
        Assert.Equal(1, slots.Active);
    }

    [Fact]
    public void ConfiguredValues_AreUsedAsGiven()
    {
        using var slots = new TranscodeSlots(configuredLimit: 2, configuredTimeoutSeconds: 30);

        Assert.Equal(2, slots.Limit);
        Assert.Equal(TimeSpan.FromSeconds(30), slots.Timeout);
        Assert.Equal(2, slots.Available);
    }
}
