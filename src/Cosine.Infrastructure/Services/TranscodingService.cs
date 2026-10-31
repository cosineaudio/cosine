using System.Diagnostics;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Services;

public class TranscodingService : ITranscodingService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MediaOptions> _mediaOptions;
    private readonly IOptions<TranscodingOptions> _transcodingOptions;
    private readonly ILogger<TranscodingService> _logger;
    private readonly Telemetry.CosineMetrics? _metrics;
    private readonly TranscodeSlots _slots;
    // Serialises cache promotion across concurrent transcodes of the same track - see
    // PromoteCacheFileAsync. The service is a singleton, so one gate covers the process.
    private readonly SemaphoreSlim _promoteGate = new(1, 1);

    // Muxer is the ffmpeg -f name, which differs from the format name for aac
    // (the "aac" muxer does not exist; raw AAC streams use the adts muxer).
    private static readonly Dictionary<string, (string Codec, string Muxer, string Extension, string ContentType)> FormatMap
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = ("libmp3lame", "mp3", "mp3", "audio/mpeg"),
        ["opus"] = ("libopus", "opus", "opus", "audio/opus"),
        ["aac"] = ("aac", "adts", "aac", "audio/aac"),
        ["flac"] = ("flac", "flac", "flac", "audio/flac"),
        ["ogg"] = ("libvorbis", "ogg", "ogg", "audio/ogg"),
        ["wav"] = ("pcm_s16le", "wav", "wav", "audio/wav"),
    };

    public TranscodingService(
        IServiceScopeFactory scopeFactory,
        IOptions<MediaOptions> mediaOptions,
        IOptions<TranscodingOptions> transcodingOptions,
        ILogger<TranscodingService> logger,
        Telemetry.CosineMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _mediaOptions = mediaOptions;
        _transcodingOptions = transcodingOptions;
        _logger = logger;
        _metrics = metrics;
        _slots = new TranscodeSlots(
            transcodingOptions.Value.MaxConcurrentTranscodes,
            transcodingOptions.Value.TranscodeQueueTimeoutSeconds);
    }

    // Every distinct bitrate is a distinct cache file and a distinct ffmpeg process, and the
    // requested value reaches here straight from the client (`stream?maxBitRate=`,
    // `getTranscodeStream?transcodeParams=mp3:1`). Left unbounded, looping 1, 2, 3, … fills the
    // cache disk and pins a core per request. Snapping to a fixed ladder caps a (track, format)
    // at this many cache entries no matter what is asked for.
    private static readonly int[] BitRateLadder = [32, 48, 64, 96, 128, 160, 192, 224, 256, 320];

    /// <summary>
    /// Snaps a requested bitrate onto <see cref="BitRateLadder"/>, rounding *down* so a
    /// <c>maxBitRate</c> cap is never exceeded, and clamping to the ladder's ends. Lossless
    /// targets and non-positive requests return 0, which means "no <c>-b:a</c>, encoder default".
    /// </summary>
    public static int NormalizeBitRate(string format, int bitRate)
    {
        if (IsLossless(format) || bitRate <= 0)
            return 0;

        if (bitRate >= BitRateLadder[^1])
            return BitRateLadder[^1];

        var rung = BitRateLadder[0];
        foreach (var candidate in BitRateLadder)
        {
            if (candidate > bitRate)
                break;
            rung = candidate;
        }
        return rung;
    }

    private static bool IsLossless(string format)
        => string.Equals(format, "flac", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase);

    public bool SupportsFormat(string format) => FormatMap.ContainsKey(format);

    public TranscodeActivity GetActivity() => new(_slots.Active, _slots.Limit);

    public string GetContentType(string format)
        => FormatMap.TryGetValue(format, out var info) ? info.ContentType : "application/octet-stream";

    public Task<IReadOnlyList<TranscodeFormat>> GetSupportedFormatsAsync(CancellationToken ct = default)
    {
        var bitrates = _transcodingOptions.Value.DefaultBitrates;
        var formats = new List<TranscodeFormat>();

        foreach (var (name, (_, _, extension, _)) in FormatMap)
        {
            var defaultBitrate = bitrates.GetValueOrDefault(name, 192);
            // Lossless formats don't offer bitrate options
            var bitrateList = name is "flac" or "wav"
                ? Array.Empty<int>()
                : new[] { defaultBitrate, defaultBitrate / 2, defaultBitrate * 2, 320 };
            formats.Add(new TranscodeFormat(name, extension, bitrateList.Distinct().Order().ToList()));
        }

        return Task.FromResult<IReadOnlyList<TranscodeFormat>>(formats);
    }

    public async Task<Stream> GetTranscodeStreamAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
    {
        var (codec, muxer, extension) = GetCodecInfo(format);

        // Normalised before the cache lookup and the cache filename are derived from it, so an
        // off-ladder request reuses the rung below it instead of minting an entry of its own.
        bitRate = NormalizeBitRate(format, bitRate);

        string fullPath;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

            var track = await db.Tracks.FindAsync([trackId], ct);
            if (track is null)
                throw new FileNotFoundException("Track not found");

            fullPath = MediaPath.Resolve(_mediaOptions.Value.RootPath, track.FilePath)
                ?? throw new FileNotFoundException("Audio file not found on disk");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Audio file not found on disk");

            var cacheEntry = await db.TranscodeCache
                .FirstOrDefaultAsync(tc => tc.TrackId == trackId
                    && tc.Format == format
                    && tc.BitRate == bitRate, ct);

            if (cacheEntry is not null && File.Exists(cacheEntry.FilePath))
            {
                cacheEntry.LastAccessedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return new FileStream(cacheEntry.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
        }

        // Transcode to stdout so the client gets audio immediately; tee into a temp
        // file alongside, promoted to a cache entry once ffmpeg finishes cleanly.
        var cachePath = _transcodingOptions.Value.CachePath;
        string? finalPath = null;
        string? tempPath = null;
        if (!string.IsNullOrEmpty(cachePath))
        {
            finalPath = Path.Combine(cachePath, $"{trackId}_{format}_{bitRate}.{extension}");
            tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _transcodingOptions.Value.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildFfmpegArgs(fullPath, muxer, codec, bitRate))
            startInfo.ArgumentList.Add(arg);

        var process = new Process { StartInfo = startInfo };

        await _slots.AcquireAsync(ct);
        FileStream? tee = null;
        try
        {
            process.Start();
            _metrics?.RecordTranscodeSpawn(format);

            // Drain stderr concurrently to avoid ffmpeg blocking on a full pipe.
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = stderrTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(t.Result))
                    _logger.LogWarning("ffmpeg stderr for track {TrackId} ({Format}:{BitRate}): {Stderr}",
                        trackId, format, bitRate, t.Result.Trim());
            }, TaskScheduler.Default);

            if (tempPath is not null)
            {
                try
                {
                    Directory.CreateDirectory(cachePath!);
                    tee = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        4096, FileOptions.Asynchronous);
                }
                catch (Exception ex)
                {
                    // A read-only or misconfigured cache dir must not break playback.
                    _logger.LogWarning(ex, "Transcode cache unavailable at {CachePath}; streaming without caching", cachePath);
                    tee = null;
                    tempPath = null;
                }
            }

            return new FfmpegStream(process, tee, tempPath, _slots, _logger,
                onCompleted: tempPath is null ? null : bytes => PromoteCacheFileAsync(tempPath, finalPath!, trackId, format, bitRate, bytes),
                onFailed: () => _metrics?.RecordTranscodeFailure(format));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            process.Dispose();
            tee?.Dispose();
            if (tempPath is not null)
                try { File.Delete(tempPath); } catch { /* ignore */ }
            _slots.Release();
            throw;
        }
    }

    /// <summary>
    /// Runs a transcode to completion into the cache and reports the finished file's length.
    ///
    /// The encode itself is <see cref="GetTranscodeStreamAsync"/>, drained to nowhere. Reading
    /// the stream is what drives ffmpeg, fills the tee and - on clean exit - promotes the temp
    /// file into a cache entry, so borrowing that path gets slot accounting, the concurrent-
    /// transcode dedup and the promotion race handling for free. A second implementation that
    /// wrote its own file would have to reproduce all three, and would drift from them.
    ///
    /// The cache is not optional here the way it is for a listener. A listener with no cache
    /// configured still gets audio, just uncached; a renderer that demands a Content-Length
    /// gets nothing at all, so an unconfigured cache is an error naming the setting rather
    /// than a transcode that turns out to have been pointless.
    /// </summary>
    public async Task<long> MaterializeAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
    {
        var cachePath = _transcodingOptions.Value.CachePath;
        if (string.IsNullOrEmpty(cachePath))
            throw new InvalidOperationException(
                "Cannot produce a transcode of known length: Transcoding:CachePath is not set. " +
                "Network renderers that require a Content-Length can only be served from the " +
                "transcode cache, because live ffmpeg output is chunked and has no length.");

        // Normalised here as well as inside GetTranscodeStreamAsync, so the entry this looks up
        // is the same rung the transcode will write to.
        bitRate = NormalizeBitRate(format, bitRate);

        if (await TryGetCachedLengthAsync(trackId, format, bitRate, ct) is { } cached)
            return cached;

        await using (var stream = await GetTranscodeStreamAsync(trackId, format, bitRate, ct))
        {
            // A cache hit came back a seekable file rather than ffmpeg output - someone
            // materialised it between the lookup above and here. Nothing left to do.
            if (stream.CanSeek)
                return stream.Length;

            await stream.CopyToAsync(Stream.Null, ct);
        }

        return await TryGetCachedLengthAsync(trackId, format, bitRate, ct)
            ?? throw new InvalidOperationException(
                $"Transcode of track {trackId} to {format}:{bitRate} finished but was not cached. " +
                $"Check that {cachePath} is writable and has free space.");
    }

    /// <summary>
    /// Length of a cached entry, or null if there isn't one. Measures the file rather than
    /// trusting the row's <c>FileSize</c>, which is nullable and is what a renderer would be
    /// told to expect - a wrong length is worse than no length, because the device commits to
    /// it and then times out waiting for bytes that never come.
    /// </summary>
    private async Task<long?> TryGetCachedLengthAsync(Guid trackId, string format, int bitRate, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

        var entry = await db.TranscodeCache.FirstOrDefaultAsync(
            tc => tc.TrackId == trackId && tc.Format == format && tc.BitRate == bitRate, ct);

        if (entry is null || !File.Exists(entry.FilePath))
            return null;

        entry.LastAccessedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new FileInfo(entry.FilePath).Length;
    }

    /// <summary>
    /// Publishes a finished temp file as the cache entry for its (track, format, bitrate).
    ///
    /// Nothing deduplicates in-flight transcodes, so N simultaneous listeners of the same
    /// track each produce their own temp file and arrive here together. Serialising the whole
    /// check-move-write keeps that from turning into a lost race on the final path (a listener
    /// may be reading it right now) and on the unique index (a concurrent read-then-insert
    /// pair produced a duplicate row). The bytes are identical either way, so the first
    /// finisher's file wins and the rest are discarded.
    /// </summary>
    private async Task PromoteCacheFileAsync(string tempPath, string finalPath, Guid trackId, string format, int bitRate, long fileSize)
    {
        await _promoteGate.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

            var existing = await db.TranscodeCache
                .FirstOrDefaultAsync(tc => tc.TrackId == trackId && tc.Format == format && tc.BitRate == bitRate);

            if (existing is not null && File.Exists(existing.FilePath))
            {
                // Somebody got here first. Overwriting their file would replace it under
                // whoever is reading it, for byte-identical output.
                existing.LastAccessedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                try { File.Delete(tempPath); } catch { /* ignore */ }
                return;
            }

            File.Move(tempPath, finalPath, overwrite: true);

            if (existing is null)
            {
                db.TranscodeCache.Add(new TranscodeCache
                {
                    TrackId = trackId,
                    Format = format,
                    BitRate = bitRate,
                    FilePath = finalPath,
                    FileSize = fileSize,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastAccessedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.FilePath = finalPath;
                existing.FileSize = fileSize;
                existing.LastAccessedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            _promoteGate.Release();
        }
    }

    public async Task<int> PruneCacheAsync(long maxDiskBytes, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

        var entries = await db.TranscodeCache
            .OrderBy(tc => tc.LastAccessedAt)
            .ToListAsync(ct);

        long totalSize = entries.Sum(e => e.FileSize ?? 0);
        int removed = 0;

        foreach (var entry in entries)
        {
            if (totalSize <= maxDiskBytes)
                break;

            if (File.Exists(entry.FilePath))
            {
                try { File.Delete(entry.FilePath); } catch { /* ignore */ }
            }

            db.TranscodeCache.Remove(entry);
            totalSize -= entry.FileSize ?? 0;
            removed++;
        }

        if (removed > 0)
            await db.SaveChangesAsync(ct);

        removed += ReclaimAbandonedTempFiles(_transcodingOptions.Value.CachePath, TempFileMaxAge, _logger);

        return removed;
    }

    /// <summary>
    /// Deletes <c>.tmp</c> tee files left behind by a transcode that was killed rather than
    /// finished. Nothing else ever reclaims them: prune only walks files that have a
    /// <c>TranscodeCache</c> row, and an abandoned temp file never got one, so a hard kill used
    /// to leak a whole song's worth of disk permanently. The age floor keeps this off temp files
    /// belonging to transcodes that are in flight right now.
    /// </summary>
    public static int ReclaimAbandonedTempFiles(string cachePath, TimeSpan maxAge, ILogger logger)
    {
        if (string.IsNullOrEmpty(cachePath) || !Directory.Exists(cachePath))
            return 0;

        var cutoff = DateTime.UtcNow - maxAge;
        var reclaimed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(cachePath, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                    File.Delete(file);
                    reclaimed++;
                }
                catch { /* raced with a live transcode, or not ours to delete */ }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sweep abandoned transcode temp files in {CachePath}", cachePath);
        }

        if (reclaimed > 0)
            logger.LogInformation("Reclaimed {Count} abandoned transcode temp files", reclaimed);

        return reclaimed;
    }

    /// <summary>A transcode outliving this is not in flight - the longest song plus slack.</summary>
    private static readonly TimeSpan TempFileMaxAge = TimeSpan.FromHours(6);

    /// <summary>
    /// Builds the argument *list*. There is no shell here (<c>UseShellExecute = false</c>), so the
    /// old hand-quoted single string was not a command-injection hole - but .NET re-parses such a
    /// string with Windows tokenisation rules, so a filename containing a double quote (legal on
    /// Linux) split into extra tokens that ffmpeg then read as options. One element per argument
    /// removes the quoting question entirely.
    /// </summary>
    public static List<string> BuildFfmpegArgs(string inputPath, string muxer, string codec, int bitRate)
    {
        var args = new List<string>
        {
            "-loglevel", "error",
            "-i", inputPath,
            // Embedded cover art arrives as a video stream; audio-only muxers reject it.
            "-vn",
            "-c:a", codec,
        };

        if (bitRate > 0)
        {
            args.Add("-b:a");
            args.Add($"{bitRate}k");
        }

        if (muxer is "flac" or "wav")
        {
            args.Add("-sample_fmt");
            args.Add("s16");
        }

        args.Add("-f");
        args.Add(muxer);
        args.Add("pipe:1");

        return args;
    }

    /// <summary>
    /// The ffmpeg wiring for a format. The content type is deliberately not part of this
    /// tuple - callers get it from <see cref="GetContentType"/>, which reads the same map.
    /// </summary>
    private static (string Codec, string Muxer, string Extension) GetCodecInfo(string format)
    {
        if (FormatMap.TryGetValue(format, out var info))
            return (info.Codec, info.Muxer, info.Extension);

        throw new NotSupportedException($"Unsupported transcoding format: {format}");
    }

    public void Dispose()
    {
        _slots.Dispose();
        _promoteGate.Dispose();
    }

    /// <summary>
    /// Streams ffmpeg stdout to the reader while teeing into a temp cache file.
    /// On dispose: kills ffmpeg if the reader stopped early, promotes the temp file
    /// via <c>onCompleted</c> if the transcode finished cleanly, and releases the
    /// transcode slot exactly once.
    /// </summary>
    private sealed class FfmpegStream : Stream
    {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly TranscodeSlots _slots;
        private readonly ILogger _logger;
        private readonly Func<long, Task>? _onCompleted;
        private readonly Action? _onFailed;
        private FileStream? _tee;
        private string? _tempPath;
        private long _bytesRead;
        private bool _reachedEof;
        private bool _disposed;

        public FfmpegStream(Process process, FileStream? tee, string? tempPath,
            TranscodeSlots slots, ILogger logger, Func<long, Task>? onCompleted,
            Action? onFailed = null)
        {
            _process = process;
            _stdout = process.StandardOutput.BaseStream;
            _tee = tee;
            _tempPath = tempPath;
            _slots = slots;
            _logger = logger;
            _onCompleted = onCompleted;
            _onFailed = onFailed;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _stdout.Read(buffer, offset, count);
            // Synchronous all the way down: blocking on an async tee write here would be a
            // deadlock shape (sync-over-async on whatever context the caller is holding).
            if (CountRead(read))
                TeeWrite(() => _tee!.Write(buffer.AsSpan(offset, read)));
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await _stdout.ReadAsync(buffer, ct);
            if (CountRead(read))
            {
                try
                {
                    await _tee!.WriteAsync(buffer[..read], ct);
                }
                catch (Exception ex)
                {
                    DropTee(ex);
                }
            }
            return read;
        }

        /// <summary>
        /// Records the read against EOF/byte-count bookkeeping. Returns true when the bytes
        /// still need teeing to the cache file.
        /// </summary>
        private bool CountRead(int read)
        {
            if (read == 0)
            {
                _reachedEof = true;
                return false;
            }

            _bytesRead += read;
            return _tee is not null;
        }

        private void TeeWrite(Action write)
        {
            try { write(); }
            catch (Exception ex) { DropTee(ex); }
        }

        private void DropTee(Exception ex)
        {
            // Cache write failure (e.g. disk full) must not interrupt playback.
            _logger.LogWarning(ex, "Dropping transcode cache tee at {TempPath}", _tempPath);
            DiscardTee();
        }

        private void DiscardTee()
        {
            try { _tee?.Dispose(); } catch { /* ignore */ }
            _tee = null;
            if (_tempPath is not null)
            {
                try { File.Delete(_tempPath); } catch { /* ignore */ }
                _tempPath = null;
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                if (_reachedEof)
                {
                    // stdout hit EOF, so ffmpeg is done writing and exits momentarily.
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await _process.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { /* fall through to kill */ }
                }

                if (!_process.HasExited)
                {
                    try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }

                var success = _reachedEof && _process.HasExited && _process.ExitCode == 0;

                // Only an encode ffmpeg finished on its own terms counts as a failure signal:
                // a listener stopping playback early kills the process without reaching EOF,
                // and that says nothing about ffmpeg's health.
                if (_reachedEof && !success)
                    _onFailed?.Invoke();

                if (_tee is not null)
                {
                    try { await _tee.DisposeAsync(); } catch { success = false; }
                    _tee = null;
                }

                if (success && _tempPath is not null && _onCompleted is not null)
                {
                    try
                    {
                        await _onCompleted(_bytesRead);
                        _tempPath = null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to promote transcode cache file {TempPath}", _tempPath);
                    }
                }

                if (_tempPath is not null)
                {
                    try { File.Delete(_tempPath); } catch { /* ignore */ }
                    _tempPath = null;
                }
            }
            finally
            {
                await _stdout.DisposeAsync();
                _process.Dispose();
                _slots.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
    }
}
