using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

/// <summary>
/// <c>getCoverArt?size=</c> was accepted and ignored, so a 60-tile album grid pulled 60
/// full-resolution covers, and every response was labelled <c>image/jpeg</c> whatever the
/// embedded picture actually was.
/// </summary>
public class ImageResizeTests
{
    // --- Format sniffing ---

    public static TheoryData<byte[], string> Signatures => new()
    {
        { [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10], "image/jpeg" },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png" },
        { "GIF89a....."u8.ToArray(), "image/gif" },
        { "BM......"u8.ToArray(), "image/bmp" },
        { "RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), "image/webp" },
        { "not an image"u8.ToArray(), ImageFormats.Fallback },
        { [], ImageFormats.Fallback },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void DetectContentType_ReadsTheMagicBytes(byte[] bytes, string expected)
        => Assert.Equal(expected, ImageFormats.DetectContentType(bytes));

    [Fact]
    public void DetectContentType_RiffThatIsNotWebp_IsNotClaimedAsAnImage()
        => Assert.Equal(ImageFormats.Fallback, ImageFormats.DetectContentType("RIFF\0\0\0\0WAVEfmt "u8));

    // --- Size clamping ---

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]           // Subsonic clients send 0 for "full size"
    [InlineData(-100, null)]
    [InlineData(1, ArtworkService.MinSize)]
    [InlineData(300, 300)]
    [InlineData(int.MaxValue, ArtworkService.MaxSize)]
    public void NormalizeSize_ClampsClientInput(int? requested, int? expected)
        => Assert.Equal(expected, ArtworkService.NormalizeSize(requested));

    // --- ArtworkService caching ---

    private static (ArtworkService Service, RecordingResizer Resizer, Infrastructure.Data.CosineDbContext Db) Setup(
        out Guid albumId, byte[]? cover = null)
    {
        var db = TestDbContextFactory.Create();
        var artist = new Artist { Id = Guid.NewGuid(), Name = "A", SortName = "a" };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Al", SortName = "al", ArtistId = artist.Id,
            CoverBlob = cover ?? [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.SaveChanges();

        albumId = album.Id;
        var resizer = new RecordingResizer();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return (new ArtworkService(db, cache, resizer), resizer, db);
    }

    [Fact]
    public async Task GetAlbumArt_WithASize_ResizesOnce_ThenServesFromCache()
    {
        var (service, resizer, db) = Setup(out var albumId);
        using var _ = db;

        var first = await service.GetAlbumArtAsync(albumId, 200);
        var second = await service.GetAlbumArtAsync(albumId, 200);

        Assert.Equal([200], resizer.Requested);
        Assert.Equal(first!.Value.Bytes, second!.Value.Bytes);
        Assert.Equal(RecordingResizer.Marker, first.Value.Bytes[0]);
    }

    [Fact]
    public async Task GetAlbumArt_DifferentSizes_AreCachedSeparately()
    {
        var (service, resizer, db) = Setup(out var albumId);
        using var _ = db;

        await service.GetAlbumArtAsync(albumId, 100);
        await service.GetAlbumArtAsync(albumId, 500);
        await service.GetAlbumArtAsync(albumId);

        // The full-size entry must not be poisoned by whichever size was asked for first.
        Assert.Equal([100, 500], resizer.Requested);
        var full = await service.GetAlbumArtAsync(albumId);
        Assert.Equal(8, full!.Value.Bytes.Length);
    }

    [Fact]
    public async Task GetAlbumArt_WhenResizingFails_ServesTheOriginal()
    {
        var (service, resizer, db) = Setup(out var albumId);
        using var _ = db;
        resizer.Fail = true;

        var art = await service.GetAlbumArtAsync(albumId, 200);

        Assert.Equal(8, art!.Value.Bytes.Length);
        Assert.Equal("image/jpeg", art.Value.ContentType);
    }

    [Fact]
    public async Task GetAlbumArt_ReportsTheActualImageFormat()
    {
        var (service, _, db) = Setup(out var albumId,
            cover: [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        using var __ = db;

        var art = await service.GetAlbumArtAsync(albumId);

        Assert.Equal("image/png", art!.Value.ContentType);
    }

    [Fact]
    public async Task GetAlbumArt_MissingAlbum_ReturnsNull()
    {
        var (service, _, db) = Setup(out _);
        using var __ = db;

        Assert.Null(await service.GetAlbumArtAsync(Guid.NewGuid(), 200));
    }

    private sealed class RecordingResizer : IImageResizer
    {
        public const byte Marker = 0xAB;

        public List<int> Requested { get; } = [];
        public bool Fail { get; set; }

        public Task<byte[]?> ResizeAsync(byte[] source, int maxEdge, CancellationToken ct = default)
        {
            Requested.Add(maxEdge);
            return Task.FromResult<byte[]?>(Fail ? null : [Marker, (byte)maxEdge]);
        }
    }

    // --- The real ffmpeg-backed resizer ---

    [FfmpegFact]
    public async Task FfmpegResizer_ScalesDownAndPreservesAspectRatio()
    {
        var source = await LoadFixtureAsync();      // 1200x800 PNG
        var resizer = CreateResizer();

        var resized = await resizer.ResizeAsync(source, 300);

        Assert.NotNull(resized);
        // PNG in, PNG out: the source may carry alpha, and a JPEG would flatten it to black.
        Assert.Equal("image/png", ImageFormats.DetectContentType(resized));
        Assert.Equal((300, 200), PngSize(resized));
    }

    [FfmpegFact]
    public async Task FfmpegResizer_NeverUpscales()
    {
        var source = await LoadFixtureAsync();
        var resizer = CreateResizer();

        var resized = await resizer.ResizeAsync(source, 4000);

        Assert.NotNull(resized);
        Assert.Equal((1200, 800), PngSize(resized));
    }

    [FfmpegFact]
    public async Task FfmpegResizer_NonImageBytes_ReturnNull()
    {
        var resizer = CreateResizer();

        Assert.Null(await resizer.ResizeAsync("this is not an image"u8.ToArray(), 200));
    }

    [Fact]
    public void ScaleFilter_EscapesCommasForTheFiltergraphParser()
        => Assert.Equal(
            "scale=w=min(iw\\,64):h=min(ih\\,64):force_original_aspect_ratio=decrease:force_divisible_by=2",
            FfmpegImageResizer.ScaleFilter(64));

    private static FfmpegImageResizer CreateResizer() => new(
        Options.Create(new TranscodingOptions()),
        new LoggerFactory().CreateLogger<FfmpegImageResizer>());

    /// <summary>
    /// ffmpeg is a hard runtime dependency of the app, but not of a checkout, so these skip
    /// rather than fail where it is absent.
    /// </summary>
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

    private static Task<byte[]> LoadFixtureAsync() =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cover.png"));

    /// <summary>Width/height out of a PNG's IHDR chunk - big-endian, at a fixed offset.</summary>
    private static (int Width, int Height) PngSize(byte[] png) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
         System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
}
