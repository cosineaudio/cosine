using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

public class StreamingServiceTests
{
    [Fact]
    public void StreamResult_Properties_AreCorrect()
    {
        using var ms = new MemoryStream([1, 2, 3]);
        var result = new StreamResult(ms, "audio/mpeg", 3, true);

        Assert.True(result.AcceptRanges);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(3, result.ContentLength);
        Assert.Same(ms, result.Stream);
    }

    // --- Range handling: the metadata the controller turns into a Content-Range header. ---
    // Regression guard: a range request must report the FULL file size as TotalLength and
    // an explicit start/end, so the controller can emit "bytes <start>-<end>/<total>" rather
    // than the malformed "bytes */<sliceLen>" that made browsers refuse to play the audio.

    private static async Task<(StreamingService svc, Track track, byte[] data, string dir, FakeTranscodingService transcoder)> SetupAsync(
        TestServiceProvider sp, int fileSize)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cosine-stream-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var data = new byte[fileSize];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var fileName = "song.flac";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), data);

        var artist = new Artist { Id = Guid.NewGuid(), Name = "A", SortName = "a" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Al", SortName = "al", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "T", SortTitle = "t",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = fileName, ContentType = "audio/flac",
            BitRate = 900,
        };
        sp.DbContext.Artists.Add(artist);
        sp.DbContext.Albums.Add(album);
        sp.DbContext.Tracks.Add(track);
        await sp.DbContext.SaveChangesAsync();

        var transcoder = new FakeTranscodingService();
        var svc = new StreamingService(sp.DbContext,
            Options.Create(new MediaOptions { RootPath = dir }),
            Options.Create(new TranscodingOptions()),
            transcoder,
            new MetricsService());
        return (svc, track, data, dir, transcoder);
    }

    private class FakeTranscodingService : ITranscodingService
    {
        public List<(Guid TrackId, string Format, int BitRate)> Requests { get; } = [];

        /// <summary>Models a fresh ffmpeg spawn - chunked output of unknown length - rather than a cache hit.</summary>
        public bool Live { get; set; }

        public Task<Stream> GetTranscodeStreamAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
        {
            Requests.Add((trackId, format, bitRate));
            Stream stream = new MemoryStream([9, 9, 9]);
            if (Live) stream = new RangeStreamWrapper(stream, 3);   // CanSeek => false
            return Task.FromResult(stream);
        }

        /// <summary>Models the cache-backed path: a real length, because that is its whole point.</summary>
        public Task<long> MaterializeAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
        {
            Requests.Add((trackId, format, bitRate));
            return Task.FromResult(3L);
        }

        public Task<IReadOnlyList<TranscodeFormat>> GetSupportedFormatsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TranscodeFormat>>([]);

        public Task<int> PruneCacheAsync(long maxDiskBytes, CancellationToken ct = default)
            => Task.FromResult(0);

        public bool SupportsFormat(string format)
            => format.ToLowerInvariant() is "mp3" or "opus" or "aac" or "flac" or "ogg" or "wav";

        public string GetContentType(string format) => $"audio/test-{format}";

        public TranscodeActivity GetActivity() => new(0, 8);
    }

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Stream_NoRange_ReturnsWholeFile_WithTotalLengthAndNoRangeMarkers()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, rangeHeader: null);

            Assert.Null(r.RangeStart);
            Assert.Null(r.RangeEnd);
            Assert.Equal(5000, r.TotalLength);
            Assert.Equal(5000, r.ContentLength);
            Assert.Equal(data, await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_ClosedRange_SlicesBytes_AndReportsFullTotal()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, rangeHeader: "bytes=0-1023");

            Assert.Equal(0, r.RangeStart);
            Assert.Equal(1023, r.RangeEnd);
            Assert.Equal(5000, r.TotalLength);       // full file, NOT the slice length
            Assert.Equal(1024, r.ContentLength);     // bytes in this response body
            Assert.Equal(data[..1024], await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_OpenEndedRange_RunsToEndOfFile()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, rangeHeader: "bytes=100-");

            Assert.Equal(100, r.RangeStart);
            Assert.Equal(4999, r.RangeEnd);
            Assert.Equal(5000, r.TotalLength);
            Assert.Equal(4900, r.ContentLength);
            Assert.Equal(data[100..], await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_SuffixRange_ServesTheLastBytes()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, rangeHeader: "bytes=-500");

            Assert.Equal(4500, r.RangeStart);
            Assert.Equal(4999, r.RangeEnd);
            Assert.Equal(500, r.ContentLength);
            Assert.Equal(5000, r.TotalLength);
            Assert.Equal(data[4500..], await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_SuffixRangeLongerThanFile_ServesWholeFile()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, rangeHeader: "bytes=-9000");

            Assert.Equal(0, r.RangeStart);
            Assert.Equal(4999, r.RangeEnd);
            Assert.Equal(data, await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    // RFC 9110: a Range header that cannot be parsed - bad unit, junk bounds, or a
    // multi-range request this server does not serve - is ignored, not an error. These all
    // used to reach a bare long.Parse and surface to the client as HTTP 500.
    [Theory]
    [InlineData("bytes=0-99,200-299")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=")]
    [InlineData("bytes=-")]
    [InlineData("items=0-99")]
    [InlineData("0-99")]
    public async Task Stream_UnusableRangeHeader_IsIgnoredAndServesWholeFile(string header)
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, rangeHeader: header);

            Assert.Null(r.RangeStart);
            Assert.Null(r.RangeEnd);
            Assert.Equal(5000, r.ContentLength);
            Assert.Equal(data, await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Syntactically valid but unsatisfiable: 416, never a zero-byte 206.
    [Theory]
    [InlineData("bytes=400-100")]
    [InlineData("bytes=-0")]
    public async Task Stream_UnsatisfiableRange_ThrowsRangeNotSatisfiable(string header)
    {
        using var sp = new TestServiceProvider();
        var (svc, track, _, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var ex = await Assert.ThrowsAsync<RangeNotSatisfiableException>(
                () => svc.StreamTrackAsync(track.Id, rangeHeader: header));
            Assert.Equal(5000, ex.TotalLength);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_RangeBeyondEof_ThrowsRangeNotSatisfiable()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, _, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var ex = await Assert.ThrowsAsync<RangeNotSatisfiableException>(
                () => svc.StreamTrackAsync(track.Id, rangeHeader: "bytes=5000-"));
            Assert.Equal(5000, ex.TotalLength);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Transcode decision: format/maxBitRate on the Subsonic stream endpoint. ---

    [Fact]
    public async Task Stream_MaxBitRateBelowSource_TranscodesToMp3AtCap()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, _, dir, transcoder) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, bitRate: 128);

            Assert.Equal((track.Id, "mp3", 128), Assert.Single(transcoder.Requests));
            Assert.Equal("audio/test-mp3", r.ContentType);
            // The fake returns a seekable MemoryStream, which models a cached
            // transcode file: seekable transcode streams advertise range support.
            Assert.True(r.AcceptRanges);
        }
        finally { Directory.Delete(dir, true); }
    }

    // A cached transcode is a plain seekable file, so its size is known. Reporting 0 described
    // it as a length-unknown live stream - the opposite of what the controller needs in order
    // to hand the request to MVC's range processing.
    [Fact]
    public async Task Stream_CachedTranscode_ReportsItsRealLength()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, _, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, format: "opus");

            Assert.True(r.AcceptRanges);
            Assert.Equal(3, r.ContentLength);
            Assert.Equal(3, r.TotalLength);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_LiveTranscode_ReportsNoLengthAndNoRanges()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, _, dir, transcoder) = await SetupAsync(sp, 5000);
        transcoder.Live = true;
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, format: "opus");

            Assert.False(r.AcceptRanges);
            Assert.Equal(0, r.ContentLength);
            Assert.Equal(0, r.TotalLength);
        }
        finally { Directory.Delete(dir, true); }
    }

    // #49: a FilePath that escapes the media root must not be streamed even when the file is
    // really there. ScanJob.ToRelativePath returns the *absolute* path for a file that does not
    // start with the root prefix, and Path.Combine drops the root when handed one.
    [Theory]
    [InlineData("../outside.flac")]
    [InlineData("a/../../outside.flac")]
    public async Task Stream_TrackPathEscapingTheMediaRoot_IsNotFound(string escapingPath)
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            // The escape target exists on disk - only containment stops it being served.
            var outside = Path.Combine(Path.GetDirectoryName(dir)!, "outside.flac");
            await File.WriteAllBytesAsync(outside, data);
            try
            {
                track.FilePath = escapingPath;
                await sp.DbContext.SaveChangesAsync();

                await Assert.ThrowsAsync<FileNotFoundException>(
                    () => svc.StreamTrackAsync(track.Id, rangeHeader: null));
            }
            finally { File.Delete(outside); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_AbsoluteTrackPath_IsNotFound()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, _) = await SetupAsync(sp, 5000);
        try
        {
            var outside = Path.Combine(Path.GetDirectoryName(dir)!, "absolute.flac");
            await File.WriteAllBytesAsync(outside, data);
            try
            {
                track.FilePath = outside;
                await sp.DbContext.SaveChangesAsync();

                await Assert.ThrowsAsync<FileNotFoundException>(
                    () => svc.StreamTrackAsync(track.Id, rangeHeader: null));
            }
            finally { File.Delete(outside); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_FormatChange_TranscodesAtDefaultBitrate()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, _, dir, transcoder) = await SetupAsync(sp, 5000);
        try
        {
            await svc.StreamTrackAsync(track.Id, format: "opus");

            // 128 is TranscodingOptions.DefaultBitrates["opus"]
            Assert.Equal((track.Id, "opus", 128), Assert.Single(transcoder.Requests));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_RawFormat_IgnoresMaxBitRate()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, transcoder) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, format: "raw", bitRate: 64);

            Assert.Empty(transcoder.Requests);
            Assert.Equal(data, await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_MaxBitRateAboveSource_ServesOriginal()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, transcoder) = await SetupAsync(sp, 5000);
        try
        {
            // track.BitRate is 900
            var r = await svc.StreamTrackAsync(track.Id, bitRate: 1000);

            Assert.Empty(transcoder.Requests);
            Assert.Equal(data, await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_FormatMatchingSource_ServesOriginal()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, transcoder) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, format: "flac");

            Assert.Empty(transcoder.Requests);
            Assert.Equal(data, await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stream_UnsupportedRequestedFormat_FallsBackToOriginal()
    {
        using var sp = new TestServiceProvider();
        var (svc, track, data, dir, transcoder) = await SetupAsync(sp, 5000);
        try
        {
            var r = await svc.StreamTrackAsync(track.Id, format: "wma");

            Assert.Empty(transcoder.Requests);
            Assert.Equal(data, await ReadAllAsync(r.Stream));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StreamResult_NoRangeSupport()
    {
        using var ms = new MemoryStream();
        var result = new StreamResult(ms, "audio/flac", 100, false);

        Assert.False(result.AcceptRanges);
    }

    [Theory]
    [InlineData("bytes=0-499", 0, 499)]
    [InlineData("bytes=100-199", 100, 199)]
    [InlineData("bytes=0-", 0, -1)]
    public void RangeHeader_Parsing(string header, int expectedStart, int expectedEnd)
    {
        // Range parsing is internal to StreamingService
        // This test validates the range format we expect clients to send
        var rangeValue = header.Replace("bytes=", "");
        var parts = rangeValue.Split('-');
        var start = long.Parse(parts[0]);
        var end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1])
            ? long.Parse(parts[1])
            : -1;

        Assert.Equal(expectedStart, (int)start);
        if (end != -1)
            Assert.Equal(expectedEnd, (int)end);
    }

    [Fact]
    public async Task RangeStream_ReadsWithinBounds()
    {
        var data = new byte[1000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        using var inner = new MemoryStream(data);
        // Simulate what RangeStream does: seek to start, wrap with length
        inner.Seek(100, SeekOrigin.Begin);
        using var rs = new RangeStreamWrapper(inner, 50);

        var buffer = new byte[30];
        var read = await rs.ReadAsync(buffer, 0, 30);

        Assert.Equal(30, read);
        Assert.Equal(100, buffer[0]); // data[100]
        Assert.Equal(129, buffer[29]); // data[129]
    }

    [Fact]
    public async Task RangeStream_DoesNotReadBeyondLength()
    {
        var data = new byte[200];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;

        using var inner = new MemoryStream(data);
        inner.Seek(10, SeekOrigin.Begin);
        using var rs = new RangeStreamWrapper(inner, 20);

        var buffer = new byte[100];
        var read = await rs.ReadAsync(buffer, 0, 100);

        Assert.Equal(20, read);
    }

    [Fact]
    public void RangeStream_CanRead_IsTrue()
    {
        using var inner = new MemoryStream();
        using var rs = new RangeStreamWrapper(inner, 10);

        Assert.True(rs.CanRead);
        Assert.False(rs.CanSeek);
        Assert.False(rs.CanWrite);
    }

    [Fact]
    public void RangeStream_Length_IsConfigured()
    {
        using var inner = new MemoryStream([1, 2, 3, 4, 5]);
        using var rs = new RangeStreamWrapper(inner, 3);

        Assert.Equal(3, rs.Length);
    }

    // Simple wrapper that mirrors the behavior of StreamingService's inner RangeStream class
    private class RangeStreamWrapper : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _position;

        public RangeStreamWrapper(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, remaining);
            var read = _inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, remaining);
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, toRead), ct);
            _position += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
