using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Playback;
using Cosine.Playback.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

public class PlaybackStreamResolverTests
{
    private const string BaseUrl = "http://192.168.1.10:8080";

    private static readonly PlaybackCapabilities Capable = new(
        SupportsSeek: true, SupportsPreload: true, SupportsVolume: true,
        RequiresContentLength: false, TransportMode.Pull,
        Codecs: ["audio/mpeg", "audio/flac"]);

    private static readonly Guid TrackId = Guid.NewGuid();
    private static readonly Guid AlbumId = Guid.NewGuid();

    /// <param name="ttlHours">
    /// Negative mints a token whose expiry is already in the past, which is how expiry is tested
    /// without waiting twelve hours or adding a clock seam nothing else needs.
    /// </param>
    private static (PlaybackStreamResolver Resolver, CosineDbContext Db) Build(
        string? baseUrl = BaseUrl, int ttlHours = 12,
        string sourceFormat = "flac", string sourceContentType = "audio/flac",
        IDataProtectionProvider? provider = null,
        FakeTranscoder? transcoder = null)
    {
        var db = TestDbContextFactory.Create();
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Boards of Canada" };
        var album = new Album { Id = AlbumId, Name = "Geogaddi", ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(new Track
        {
            Id = TrackId,
            Title = "1969",
            AlbumId = album.Id,
            ArtistId = artist.Id,
            DurationMs = 274_000,
            Format = sourceFormat,
            ContentType = sourceContentType,
            FilePath = "boc/geogaddi/03.flac",
            FileSize = 12_345_678,
        });
        db.SaveChanges();

        var resolver = new PlaybackStreamResolver(
            provider ?? new EphemeralDataProtectionProvider(),
            db,
            Options.Create(new PlaybackOptions { AdvertisedBaseUrl = baseUrl, StreamTokenTtlHours = ttlHours }),
            Options.Create(new TranscodingOptions()),
            transcoder ?? new FakeTranscoder(),
            NullLogger<PlaybackStreamResolver>.Instance);

        return (resolver, db);
    }

    /// <summary>
    /// Stands in for the real transcoder so the format decision can be tested without ffmpeg.
    /// Only the two members the resolver actually calls do anything; the rest throw, so a
    /// resolver that started reaching for one would fail loudly rather than quietly.
    /// </summary>
    private sealed class FakeTranscoder : ITranscodingService
    {
        public List<(Guid TrackId, string Format, int BitRate)> Materialized { get; } = [];

        /// <summary>Length the materialised file reports. Distinct from the source file's size.</summary>
        public long Length { get; set; } = 4_567_890;

        /// <summary>Thrown from <see cref="MaterializeAsync"/> when set, to model a broken cache.</summary>
        public Exception? Failure { get; set; }

        /// <summary>False models ffmpeg having no encoder for the target format.</summary>
        public bool Supported { get; set; } = true;

        public Task<long> MaterializeAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
        {
            Materialized.Add((trackId, format, bitRate));
            return Failure is not null ? Task.FromException<long>(Failure) : Task.FromResult(Length);
        }

        public bool SupportsFormat(string format) => Supported;

        public Task<Stream> GetTranscodeStreamAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TranscodeFormat>> GetSupportedFormatsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> PruneCacheAsync(long maxDiskBytes, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string GetContentType(string format) => throw new NotSupportedException();

        public TranscodeActivity GetActivity() => throw new NotSupportedException();
    }

    private static Task<TrackPresentation> Resolve(
        PlaybackStreamResolver resolver, Guid sessionId, Guid userId,
        PlaybackCapabilities? caps = null, string? profile = null) =>
        resolver.ResolveAsync(sessionId, TrackId, userId, caps ?? Capable, profile, CancellationToken.None);

    // ---- Tokens ----

    [Fact]
    public async Task Token_RoundTrips_EveryTicketField()
    {
        var (resolver, _) = Build();
        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var presentation = await Resolve(resolver, sessionId, userId, profile: "mp3:320");
        var token = TokenFrom(presentation.Url, "stream");

        Assert.True(resolver.TryValidateToken(token, out var ticket));
        Assert.Equal(sessionId, ticket.SessionId);
        Assert.Equal(TrackId, ticket.TrackId);
        Assert.Equal(userId, ticket.UserId);
        Assert.Equal("mp3", ticket.Format);
        Assert.Equal(320, ticket.BitRate);
    }

    [Fact]
    public async Task Token_ForADirectServe_CarriesNoFormat()
    {
        var (resolver, _) = Build();

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));

        Assert.Null(ticket.Format);
        Assert.Null(ticket.BitRate);
        Assert.Equal("audio/flac", presentation.ContentType);
    }

    [Fact]
    public async Task Token_IsNotOneShot()
    {
        // Renderers HEAD-probe, issue several ranged GETs and reconnect on seek. A nonce that
        // burned on first use would break all of that.
        var (resolver, _) = Build();
        var token = TokenFrom((await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid())).Url, "stream");

        Assert.True(resolver.TryValidateToken(token, out _));
        Assert.True(resolver.TryValidateToken(token, out _));
        Assert.True(resolver.TryValidateToken(token, out _));
    }

    [Fact]
    public async Task Token_IsRejected_WhenTampered()
    {
        var (resolver, _) = Build();
        var token = TokenFrom((await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid())).Url, "stream");

        var tampered = token[..^4] + (token.EndsWith("AAAA") ? "BBBB" : "AAAA");

        Assert.False(resolver.TryValidateToken(tampered, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("!!!!not base64!!!!")]
    public void Token_IsRejected_WhenMalformed(string token)
    {
        var (resolver, _) = Build();
        Assert.False(resolver.TryValidateToken(token, out _));
    }

    [Fact]
    public async Task Token_IsRejected_WhenExpired()
    {
        var (resolver, _) = Build(ttlHours: -1);
        var token = TokenFrom((await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid())).Url, "stream");

        Assert.False(resolver.TryValidateToken(token, out _));
    }

    [Fact]
    public async Task Token_IsRejected_ByADifferentKeyRing()
    {
        var (minter, _) = Build();
        var (other, _) = Build(provider: new EphemeralDataProtectionProvider());

        var token = TokenFrom((await Resolve(minter, Guid.NewGuid(), Guid.NewGuid())).Url, "stream");

        Assert.False(other.TryValidateToken(token, out _));
    }

    [Fact]
    public async Task StreamToken_IsNotValid_OnTheCoverArtRoute()
    {
        // Separate protector purposes, so neither token is usable on the other's endpoint.
        var (resolver, _) = Build();
        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid());

        Assert.False(resolver.TryValidateCoverArtToken(TokenFrom(presentation.Url, "stream"), out _));
        Assert.False(resolver.TryValidateToken(TokenFrom(presentation.CoverArtUrl!, "coverart"), out _));
    }

    [Fact]
    public async Task CoverArtToken_RoundTrips()
    {
        var (resolver, _) = Build();
        var sessionId = Guid.NewGuid();

        var presentation = await Resolve(resolver, sessionId, Guid.NewGuid());

        Assert.True(resolver.TryValidateCoverArtToken(TokenFrom(presentation.CoverArtUrl!, "coverart"), out var ticket));
        Assert.Equal(sessionId, ticket.SessionId);
        Assert.Equal(AlbumId, ticket.AlbumId);
    }

    [Fact]
    public async Task Session_IsBoundIntoTheToken()
    {
        var (resolver, _) = Build();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var tokenA = TokenFrom((await Resolve(resolver, a, Guid.NewGuid())).Url, "stream");
        var tokenB = TokenFrom((await Resolve(resolver, b, Guid.NewGuid())).Url, "stream");

        Assert.True(resolver.TryValidateToken(tokenA, out var ticketA));
        Assert.True(resolver.TryValidateToken(tokenB, out var ticketB));
        Assert.Equal(a, ticketA.SessionId);
        Assert.Equal(b, ticketB.SessionId);
        Assert.NotEqual(tokenA, tokenB);
    }

    // ---- Configuration ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsConfigured_IsFalse_WithoutAnAdvertisedBaseUrl(string? baseUrl)
    {
        var (resolver, _) = Build(baseUrl: baseUrl);
        Assert.False(resolver.IsConfigured);
    }

    [Fact]
    public async Task Resolve_Throws_WithoutAnAdvertisedBaseUrl()
    {
        var (resolver, _) = Build(baseUrl: null);

        await Assert.ThrowsAsync<PlaybackNotConfiguredException>(
            () => Resolve(resolver, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Url_IsAbsolute_AndDoesNotDoubleUpSlashes()
    {
        var (resolver, _) = Build(baseUrl: "http://192.168.1.10:8080/");

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid());

        Assert.StartsWith("http://192.168.1.10:8080/playback/stream/", presentation.Url);
        Assert.DoesNotContain("//playback", presentation.Url);
    }

    [Fact]
    public async Task Resolve_Throws_ForAnUnknownTrack()
    {
        var (resolver, _) = Build();

        await Assert.ThrowsAsync<PlaybackTrackNotFoundException>(
            () => resolver.ResolveAsync(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Capable, null, CancellationToken.None));
    }

    [Fact]
    public async Task Presentation_CarriesLibraryMetadata()
    {
        var (resolver, _) = Build();

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal("1969", presentation.Title);
        Assert.Equal("Boards of Canada", presentation.Artist);
        Assert.Equal("Geogaddi", presentation.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(274_000), presentation.Duration);
    }

    // ---- Format choice ----

    [Fact]
    public async Task Format_ServesDirect_WhenTheDeviceListsTheSourceCodec()
    {
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac");

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal("audio/flac", presentation.ContentType);
        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));
        Assert.Null(ticket.Format);
    }

    [Fact]
    public async Task Format_FallsBackToMp3_WhenTheDeviceDeclaresNoCodecs()
    {
        // An empty list means the device told us nothing, not that it supports nothing.
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac");
        var silent = Capable with { Codecs = [] };

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: silent);

        Assert.Equal("audio/mpeg", presentation.ContentType);
        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));
        Assert.Equal("mp3", ticket.Format);
    }

    [Fact]
    public async Task Format_ServesDirect_WhenTheSourceIsAlreadyMp3AndTheDeviceIsSilent()
    {
        var (resolver, _) = Build(sourceFormat: "mp3", sourceContentType: "audio/mpeg");
        var silent = Capable with { Codecs = [] };

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: silent);

        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));
        Assert.Null(ticket.Format);
    }

    [Fact]
    public async Task Format_TranscodesToACodecTheDeviceAccepts()
    {
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac");
        var mp3Only = Capable with { Codecs = ["audio/mpeg"] };

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: mp3Only);

        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));
        Assert.Equal("mp3", ticket.Format);
    }

    [Fact]
    public async Task Format_MaterialisesTheTranscode_WhenTheDeviceNeedsALength()
    {
        // A live transcode is chunked with no length, and such a device rejects that outright.
        // Encoding into the cache first is what turns the renderer's own fetch into a plain file
        // with a real length - which is the whole reason this path exists.
        var transcoder = new FakeTranscoder { Length = 4_567_890 };
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac", transcoder: transcoder);
        var strict = Capable with { Codecs = ["audio/mpeg"], RequiresContentLength = true };

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: strict);

        Assert.Equal(4_567_890, presentation.ContentLength);
        var (_, format, _) = Assert.Single(transcoder.Materialized);
        Assert.Equal("mp3", format);
    }

    [Fact]
    public async Task Format_DoesNotMaterialise_WhenTheDeviceToleratesChunked()
    {
        // Waiting out a whole encode before playback can start is a real cost. A device that
        // does not need a length must not pay it.
        var transcoder = new FakeTranscoder();
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac", transcoder: transcoder);
        var lenient = Capable with { Codecs = ["audio/mpeg"], RequiresContentLength = false };

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: lenient);

        Assert.Null(presentation.ContentLength);
        Assert.Empty(transcoder.Materialized);
    }

    [Fact]
    public async Task Format_DirectServe_ReportsTheSourceFileSize()
    {
        // The library already knows this. Phase 1 returned null here, which cost the DIDL-Lite
        // `size` attribute on every direct play - and some renderers refuse a track without it.
        var transcoder = new FakeTranscoder();
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac", transcoder: transcoder);
        var strict = Capable with { Codecs = ["audio/flac"], RequiresContentLength = true };

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: strict);

        Assert.Equal(12_345_678, presentation.ContentLength);
        Assert.Empty(transcoder.Materialized);
    }

    [Fact]
    public async Task Format_Throws_WhenThereIsNoEncoderForTheTarget()
    {
        var transcoder = new FakeTranscoder { Supported = false };
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac", transcoder: transcoder);
        var strict = Capable with { Codecs = ["audio/mpeg"], RequiresContentLength = true };

        await Assert.ThrowsAsync<PlaybackFormatUnavailableException>(
            () => Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: strict));
        Assert.Empty(transcoder.Materialized);
    }

    [Fact]
    public async Task Format_Throws_WhenMaterialisationFails()
    {
        // An unwritable or unconfigured cache reads as broken hardware if the device is simply
        // handed a URL that serves nothing, so it surfaces as a playback error naming the cause.
        var transcoder = new FakeTranscoder
        {
            Failure = new InvalidOperationException("Transcoding:CachePath is not set."),
        };
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac", transcoder: transcoder);
        var strict = Capable with { Codecs = ["audio/mpeg"], RequiresContentLength = true };

        var ex = await Assert.ThrowsAsync<PlaybackFormatUnavailableException>(
            () => Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: strict));
        Assert.Contains("CachePath", ex.Message);
    }

    [Fact]
    public async Task Format_HonoursAnExplicitProfile_EvenForADeviceNeedingALength()
    {
        // The operator set this deliberately, so it wins over anything inferred from the
        // device's advertised codecs.
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac");
        var strict = Capable with { Codecs = ["audio/mpeg"], RequiresContentLength = true };

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), caps: strict, profile: "mp3:256");

        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));
        Assert.Equal("mp3", ticket.Format);
        Assert.Equal(256, ticket.BitRate);
    }

    [Fact]
    public async Task Format_ProfileNamingTheSourceFormat_ServesDirect()
    {
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac");

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), profile: "flac");

        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));
        Assert.Null(ticket.Format);
    }

    [Fact]
    public async Task Format_ProfileWithoutABitrate_UsesTheConfiguredDefault()
    {
        var (resolver, _) = Build(sourceFormat: "flac", sourceContentType: "audio/flac");

        var presentation = await Resolve(resolver, Guid.NewGuid(), Guid.NewGuid(), profile: "mp3");

        Assert.True(resolver.TryValidateToken(TokenFrom(presentation.Url, "stream"), out var ticket));
        Assert.Equal(new TranscodingOptions().DefaultBitrates["mp3"], ticket.BitRate);
    }

    private static string TokenFrom(string url, string route)
    {
        var marker = $"/playback/{route}/";
        var at = url.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{url}' is not a /playback/{route}/ URL");
        return url[(at + marker.Length)..];
    }
}
