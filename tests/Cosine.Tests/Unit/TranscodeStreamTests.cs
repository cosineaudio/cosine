using System.Diagnostics;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

/// <summary>
/// End-to-end transcodes against the real ffmpeg, covering the two issues in the streaming
/// path: #39 (a slot is held for the whole song, and the wait past the limit was unbounded)
/// and #50 (the synchronous <c>Read</c> blocked on the async tee write - a deadlock shape).
/// Skips where ffmpeg is not on PATH, like <c>ImageResizeTests</c>.
/// </summary>
public sealed class TranscodeStreamTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cosine-transcode-test").FullName;
    private readonly TestServiceProvider _services = new();

    public void Dispose()
    {
        _services.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private TranscodingService CreateService(int maxConcurrent = 0, int queueTimeoutSeconds = 0) => new(
        _services.ScopeFactory,
        Options.Create(new MediaOptions { RootPath = Path.Combine(_root, "media") }),
        Options.Create(new TranscodingOptions
        {
            CachePath = Path.Combine(_root, "cache"),
            MaxConcurrentTranscodes = maxConcurrent,
            TranscodeQueueTimeoutSeconds = queueTimeoutSeconds,
        }),
        NullLogger<TranscodingService>.Instance);

    /// <summary>Writes a one-second tone into the media root and returns its track id.</summary>
    private async Task<Guid> AddTrackAsync(string name = "tone.wav")
    {
        var mediaRoot = Path.Combine(_root, "media");
        Directory.CreateDirectory(mediaRoot);
        var path = Path.Combine(mediaRoot, name);

        var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            ArgumentList = { "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", path },
            RedirectStandardError = true,
        })!;
        await ffmpeg.WaitForExitAsync();
        Assert.True(File.Exists(path), await ffmpeg.StandardError.ReadToEndAsync());

        var db = _services.DbContext;
        var artist = new Artist { Id = Guid.NewGuid(), Name = "A", SortName = "a" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Al", SortName = "al", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = "T",
            SortTitle = "t",
            FilePath = name,
            ArtistId = artist.Id,
            AlbumId = album.Id,
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track.Id;
    }

    [FfmpegFact]
    public async Task AsyncRead_ProducesAudioAndPromotesTheCacheFile()
    {
        var service = CreateService();
        var trackId = await AddTrackAsync();

        var output = new MemoryStream();
        await using (var stream = await service.GetTranscodeStreamAsync(trackId, "mp3", 128))
            await stream.CopyToAsync(output);

        Assert.True(output.Length > 0);
        var cached = await _services.DbContext.TranscodeCache.SingleAsync();
        Assert.Equal(output.Length, cached.FileSize);
        Assert.Equal(output.Length, new FileInfo(cached.FilePath).Length);
    }

    [FfmpegFact]
    public async Task SyncRead_TeesTheSameBytesWithoutBlockingOnAsyncWork()
    {
        var service = CreateService();
        var trackId = await AddTrackAsync();

        var output = new MemoryStream();
        await using (var stream = await service.GetTranscodeStreamAsync(trackId, "mp3", 128))
            stream.CopyTo(output);       // Stream.CopyTo → the synchronous Read override

        Assert.True(output.Length > 0);
        var cached = await _services.DbContext.TranscodeCache.SingleAsync();
        Assert.Equal(output.Length, cached.FileSize);
        Assert.Equal(output.ToArray(), await File.ReadAllBytesAsync(cached.FilePath));
    }

    [FfmpegFact]
    public async Task SlotIsReleasedWhenTheListenerStops()
    {
        var service = CreateService(maxConcurrent: 1, queueTimeoutSeconds: 1);
        var trackId = await AddTrackAsync();

        for (var i = 0; i < 3; i++)
        {
            // Each pass must get the one slot back from the pass before it.
            await using var stream = await service.GetTranscodeStreamAsync(trackId, "flac", 0);
            await stream.CopyToAsync(Stream.Null);
        }
    }

    [FfmpegFact]
    public async Task SecondListenerPastTheLimit_FailsFastInsteadOfHanging()
    {
        var service = CreateService(maxConcurrent: 1, queueTimeoutSeconds: 1);
        var trackId = await AddTrackAsync();

        // Held open: the slot stays taken for as long as this listener exists.
        await using var held = await service.GetTranscodeStreamAsync(trackId, "flac", 0);

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TranscodeCapacityException>(
            () => service.GetTranscodeStreamAsync(trackId, "mp3", 128));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    // --- #46: nothing deduplicates in-flight transcodes, so simultaneous listeners of the same
    // (track, format, bitrate) each finished with their own temp file and each published it. ---

    [FfmpegFact]
    public async Task ConcurrentTranscodesOfOneTrack_PublishOneCacheEntry()
    {
        var service = CreateService();
        var trackId = await AddTrackAsync();

        var outputs = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var buffer = new MemoryStream();
            await using var stream = await service.GetTranscodeStreamAsync(trackId, "mp3", 128);
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }));

        // One row, not one per listener - the duplicate insert used to race the unique index.
        var cached = await _services.DbContext.TranscodeCache.SingleAsync();
        // The published file is one listener's complete output, not a half-overwritten mix.
        var onDisk = await File.ReadAllBytesAsync(cached.FilePath);
        Assert.Equal(outputs[0], onDisk);
        Assert.Equal(onDisk.Length, cached.FileSize);
        // No temp files left behind by the listeners that lost.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "cache"), "*.tmp"));
    }

    private sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute()
        {
            if (!Available.Value) Skip = "ffmpeg is not on PATH";
        }

        private static readonly Lazy<bool> Available = new(() =>
            (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, "ffmpeg"))));
    }
}
