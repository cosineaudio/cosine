using Cosine.Api.Controllers;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// <c>download</c> advertised <c>Accept-Ranges: bytes</c> and then refused ranges, and
/// <c>getCoverArt</c> emitted no validators at all, so every tile was re-fetched on every
/// page load.
/// </summary>
public class MediaRetrievalControllerTests
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static MediaRetrievalController CreateController(
        FakeArtwork? artwork = null, FakeStreaming? streaming = null)
    {
        var controller = new MediaRetrievalController(
            streaming ?? new FakeStreaming(), artwork ?? new FakeArtwork(), new FakeLyrics());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    // --- download ---

    [Fact]
    public async Task Download_ServesRanges()
    {
        var controller = CreateController();

        var result = Assert.IsType<FileStreamResult>(await controller.Download(Guid.NewGuid().ToString()));

        // A resuming client that sends a Range must get a 206, not a full 200 body it then
        // appends to what it already has.
        Assert.True(result.EnableRangeProcessing);
    }

    [Fact]
    public async Task Download_UnknownId_IsATrackNotFoundError()
    {
        var controller = CreateController();

        await Assert.ThrowsAsync<Api.Middleware.SubsonicException>(() => controller.Download("not-a-guid"));
    }

    // --- stream ---

    [Fact]
    public async Task Stream_RawFile_IsCacheableSoAPreloadedBodyCanBeReused()
    {
        var controller = CreateController();

        Assert.IsType<FileStreamResult>(await controller.Stream(Guid.NewGuid().ToString()));

        // Without these the preloader's warm-up fetch and the playing element's fetch are two
        // independent downloads of the same file (#41).
        Assert.Equal("\"5-abc\"", controller.Response.Headers.ETag.ToString());
        Assert.Contains("max-age", controller.Response.Headers.CacheControl.ToString());
        Assert.False(string.IsNullOrEmpty(controller.Response.Headers.LastModified.ToString()));
    }

    [Fact]
    public async Task Stream_LiveTranscode_IsNotCacheable()
    {
        var controller = CreateController(streaming: new FakeStreaming { ETag = null });

        Assert.IsType<FileStreamResult>(await controller.Stream(Guid.NewGuid().ToString()));

        Assert.True(string.IsNullOrEmpty(controller.Response.Headers.ETag.ToString()));
        Assert.True(string.IsNullOrEmpty(controller.Response.Headers.CacheControl.ToString()));
    }

    [Fact]
    public async Task Stream_MatchingIfNoneMatch_Returns304AndDoesNotHoldTheFileOpen()
    {
        var streaming = new FakeStreaming();
        var controller = CreateController(streaming: streaming);
        controller.Request.Headers.IfNoneMatch = streaming.ETag;

        var result = await controller.Stream(Guid.NewGuid().ToString());

        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.False(streaming.LastStream.CanRead);
    }

    [Fact]
    public async Task Stream_RangeRequestWithAMatchingTag_StillServesTheBytes()
    {
        var streaming = new FakeStreaming();
        var controller = CreateController(streaming: streaming);
        controller.Request.Headers.IfNoneMatch = streaming.ETag;
        controller.Request.Headers.Range = "bytes=0-1";

        var result = await controller.Stream(Guid.NewGuid().ToString());

        Assert.IsType<FileStreamResult>(result);
        Assert.Equal("\"5-abc\"", controller.Response.Headers.ETag.ToString());
    }

    // --- getCoverArt ---

    [Fact]
    public async Task GetCoverArt_EmitsValidatorsAndTheRealContentType()
    {
        var controller = CreateController();

        var result = Assert.IsType<FileContentResult>(await controller.GetCoverArt(Guid.NewGuid().ToString()));

        Assert.Equal("image/png", result.ContentType);
        Assert.False(string.IsNullOrEmpty(controller.Response.Headers.ETag));
        Assert.Contains("max-age", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetCoverArt_MatchingIfNoneMatch_Returns304()
    {
        var id = Guid.NewGuid().ToString();
        var controller = CreateController();
        var first = Assert.IsType<FileContentResult>(await controller.GetCoverArt(id));
        var etag = controller.Response.Headers.ETag.ToString();

        var conditional = CreateController();
        conditional.Request.Headers.IfNoneMatch = etag;
        var second = await conditional.GetCoverArt(id);

        Assert.NotNull(first);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(second).StatusCode);
    }

    [Fact]
    public async Task GetCoverArt_DifferentSizes_GetDifferentTags()
    {
        var a = CreateController();
        var b = CreateController();
        var id = Guid.NewGuid().ToString();

        await a.GetCoverArt(id, size: 100);
        await b.GetCoverArt(id, size: 400);

        // A stale tag shared across sizes would let the browser serve a thumbnail as a full cover.
        Assert.NotEqual(a.Response.Headers.ETag.ToString(), b.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task GetCoverArt_PassesTheRequestedSizeToTheArtistFallback()
    {
        var artwork = new FakeArtwork { AlbumArt = null };
        var controller = CreateController(artwork);

        await controller.GetCoverArt(Guid.NewGuid().ToString(), size: 250);

        Assert.Equal(250, artwork.ArtistSize);
    }

    [Fact]
    public async Task GetCoverArt_NoArtworkAnywhere_Is404()
    {
        var controller = CreateController(new FakeArtwork { AlbumArt = null, ArtistArt = null });

        Assert.IsType<NotFoundResult>(await controller.GetCoverArt(Guid.NewGuid().ToString()));
    }

    private sealed class FakeArtwork : IArtworkService
    {
        public byte[]? AlbumArt { get; set; } = Png;
        public byte[]? ArtistArt { get; set; } = Png;
        public int? ArtistSize { get; private set; }

        public Task<(byte[] Bytes, string ContentType)?> GetAlbumArtAsync(Guid albumId, int? size = null, CancellationToken ct = default)
            => Task.FromResult(Result(AlbumArt, size));

        public Task<(byte[] Bytes, string ContentType)?> GetArtistArtAsync(Guid artistId, int? size = null, CancellationToken ct = default)
        {
            ArtistSize = size;
            return Task.FromResult(Result(ArtistArt, size));
        }

        // Distinct bytes per size, as a real resize produces.
        private static (byte[] Bytes, string ContentType)? Result(byte[]? art, int? size) =>
            art is null ? null : ([.. art, (byte)(size ?? 0)], "image/png");
    }

    private sealed class FakeStreaming : IStreamingService
    {
        /// <summary>Null models a live transcode, which has no stable identity to validate against.</summary>
        public string? ETag { get; set; } = "\"5-abc\"";

        public MemoryStream LastStream { get; private set; } = new();

        public Task<StreamResult> StreamTrackAsync(Guid trackId, string? format = null, int? bitRate = null, string? rangeHeader = null, CancellationToken ct = default)
        {
            LastStream = new MemoryStream([1, 2, 3, 4, 5]);
            return Task.FromResult(new StreamResult(
                LastStream, "audio/flac", LastStream.Length, true, LastStream.Length,
                ETag: ETag,
                LastModified: ETag is null ? null : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }
    }

    private sealed class FakeLyrics : ILyricsService
    {
        public Task<IReadOnlyList<Core.Dtos.StructuredLyricDto>> GetStructuredLyricsAsync(Guid trackId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Core.Dtos.StructuredLyricDto>>([]);

        public Task<Core.Dtos.PlainLyricDto?> GetPlainLyricsAsync(string? artist, string? title, CancellationToken ct = default)
            => Task.FromResult<Core.Dtos.PlainLyricDto?>(null);
    }
}
