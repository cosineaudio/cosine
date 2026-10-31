using System.Net;
using System.Text;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

/// <summary>
/// The accurate half of multi-artist support. It exists because the credit is usually not in
/// the files at all: an artist→album→track tagger has nowhere to put a featured artist, so it
/// writes the release artist into every track's ARTIST frame. On the library that prompted this,
/// 1 195 of 1 200 sampled FLACs had ARTIST identical to ALBUMARTIST and not one had a
/// multi-valued artist field - the guests are only recoverable from MusicBrainz.
/// </summary>
public class MusicBrainzCreditServiceTests
{
    private const string PouyaMbid = "cae09884-f473-460a-a04a-4dd3fc69cf6e";
    private const string FatNickMbid = "8739d68e-a0f2-4073-8873-8e5efe693b04";
    private const string ReleaseMbid = "16f2affe-a918-4ebf-940b-dcc04a6e71eb";
    private const string RecordingMbid = "275631a0-92bf-4b95-8395-8f342947355d";
    private const string ReleaseTrackMbid = "ffe2b92e-d31e-452b-90f4-e969dc806745";

    /// <summary>
    /// The shape MusicBrainz actually returns for
    /// <c>/ws/2/release/{id}?inc=recordings+artist-credits</c>, trimmed to the fields read.
    /// </summary>
    private static string ReleaseJson(
        string? recordingId = RecordingMbid,
        string? trackId = ReleaseTrackMbid,
        int position = 7)
    {
        var recording = recordingId is null
            ? ""
            : $$"""" , "recording": { "id": "{{recordingId}}", "title": "Run It Up" } """";
        var id = trackId is null ? "" : $""" "id": "{trackId}", """;

        return $$"""
        {
          "id": "{{ReleaseMbid}}",
          "title": "Drop Out of School",
          "media": [
            {
              "position": 1,
              "tracks": [
                {
                  {{id}}
                  "position": {{position}},
                  "title": "Run It Up",
                  "artist-credit": [
                    { "name": "Pouya", "joinphrase": " & ",
                      "artist": { "id": "{{PouyaMbid}}", "name": "Pouya", "sort-name": "Pouya" } },
                    { "name": "Fat Nick", "joinphrase": "",
                      "artist": { "id": "{{FatNickMbid}}", "name": "Fat Nick", "sort-name": "Fat Nick" } }
                  ]
                  {{recording}}
                }
              ]
            }
          ]
        }
        """;
    }

    private sealed record Seeded(Album Album, Artist Pouya, Track Track);

    private static async Task<Seeded> SeedAsync(
        TestServiceProvider sp, string? recordingId = RecordingMbid, string? releaseTrackId = ReleaseTrackMbid,
        int? trackNumber = 7)
    {
        var db = sp.DbContext;
        var pouya = new Artist
        {
            Id = Guid.NewGuid(), Name = "Pouya", SortName = "pouya", MusicBrainzId = PouyaMbid
        };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Drop Out of School", SortName = "drop out of school",
            ArtistId = pouya.Id, MusicBrainzId = ReleaseMbid
        };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Run It Up", SortTitle = "run it up",
            AlbumId = album.Id, ArtistId = pouya.Id, FilePath = "run.flac",
            DiscNumber = 1, TrackNumber = trackNumber,
            MusicBrainzId = releaseTrackId, MusicBrainzRecordingId = recordingId
        };
        db.Artists.Add(pouya);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return new Seeded(album, pouya, track);
    }

    private static async Task EnableAsync(TestServiceProvider sp)
    {
        var settings = sp.Get<SettingsService>();
        await settings.SetAsync(IMusicBrainzCreditService.EnabledSettingKey, "true");
        // Keeps the suite off the wall clock; the production default is a 1.1 s delay.
        await settings.SetAsync(IMusicBrainzCreditService.RateLimitSettingKey, "0");
    }

    private static MusicBrainzCreditService Create(TestServiceProvider sp, RecordingHandler handler)
        => new(sp.ScopeFactory, new StubHttpClientFactory(handler), sp.Cache,
            NullLogger<MusicBrainzCreditService>.Instance);

    private static async Task<List<TrackArtist>> CreditsOf(TestServiceProvider sp, Guid trackId)
        => await sp.DbContext.TrackArtists
            .Where(ta => ta.TrackId == trackId)
            .OrderBy(ta => ta.Ordinal)
            .ToListAsync();

    // ---- the opt-out ---------------------------------------------------------------------

    /// <summary>
    /// The setting is the whole privacy contract: while it is off, a self-hosted server tells
    /// MusicBrainz nothing about what is in the library.
    /// </summary>
    [Fact]
    public async Task Disabled_ContactsNobody()
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp);

        var handler = new RecordingHandler(ReleaseJson());

        Assert.Equal(0, await Create(sp, handler).EnrichMissingCreditsAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DisabledIsTheDefault()
    {
        using var sp = new TestServiceProvider();

        Assert.Equal("false", sp.Get<SettingsService>().Defaults[IMusicBrainzCreditService.EnabledSettingKey]);
    }

    // ---- matching -------------------------------------------------------------------------

    [Fact]
    public async Task Enabled_WritesTheCreditFromTheRecordingId()
    {
        using var sp = new TestServiceProvider();
        var seeded = await SeedAsync(sp);
        await EnableAsync(sp);

        var handler = new RecordingHandler(ReleaseJson());
        Assert.Equal(1, await Create(sp, handler).EnrichMissingCreditsAsync());

        var credits = await CreditsOf(sp, seeded.Track.Id);
        Assert.Equal(["Pouya", "Fat Nick"], credits.Select(c => c.CreditedAs));
        Assert.Equal(" & ", credits[0].JoinPhrase);
        Assert.Equal("", credits[1].JoinPhrase);
        Assert.All(credits, c => Assert.Equal(TrackArtistSource.MusicBrainz, c.Source));

        // The guest is a real artist row, carrying the id that makes them linkable.
        var fatNick = Assert.Single(sp.DbContext.Artists.Where(a => a.Name == "Fat Nick"));
        Assert.Equal(FatNickMbid, fatNick.MusicBrainzId);
        Assert.Equal(fatNick.Id, credits[1].ArtistId);
    }

    /// <summary>
    /// The library's own tracks may carry only the release-track id - which is what the scanner
    /// stored as <c>MusicBrainzId</c> before the recording id was kept at all, so every track
    /// scanned before this change matches on this path.
    /// </summary>
    [Fact]
    public async Task MatchesOnTheReleaseTrackId_WhenTheRecordingIdIsMissing()
    {
        using var sp = new TestServiceProvider();
        var seeded = await SeedAsync(sp, recordingId: null);
        await EnableAsync(sp);

        Assert.Equal(1, await Create(sp, new RecordingHandler(ReleaseJson())).EnrichMissingCreditsAsync());
        Assert.Equal(["Pouya", "Fat Nick"], (await CreditsOf(sp, seeded.Track.Id)).Select(c => c.CreditedAs));
    }

    /// <summary>An untagged rip still matches, on disc and track number.</summary>
    [Fact]
    public async Task MatchesOnPosition_WhenNeitherIdIsPresent()
    {
        using var sp = new TestServiceProvider();
        var seeded = await SeedAsync(sp, recordingId: null, releaseTrackId: null);
        await EnableAsync(sp);

        Assert.Equal(1, await Create(sp, new RecordingHandler(ReleaseJson())).EnrichMissingCreditsAsync());
        Assert.Equal(["Pouya", "Fat Nick"], (await CreditsOf(sp, seeded.Track.Id)).Select(c => c.CreditedAs));
    }

    /// <summary>
    /// A release whose track list does not line up with the files must write nothing rather
    /// than credit the wrong track. This is the mis-tagged-album case.
    /// </summary>
    [Fact]
    public async Task NoMatchingTrack_WritesNothing()
    {
        using var sp = new TestServiceProvider();
        var seeded = await SeedAsync(sp, recordingId: null, releaseTrackId: null, trackNumber: 99);
        await EnableAsync(sp);

        Assert.Equal(0, await Create(sp, new RecordingHandler(ReleaseJson())).EnrichMissingCreditsAsync());
        Assert.Empty(await CreditsOf(sp, seeded.Track.Id));
    }

    /// <summary>
    /// The primary artist is not re-homed by a disagreeing release. Album grouping, the artist
    /// index and every star and playlist row hang off <c>Track.ArtistId</c>; the credit is
    /// additive and says who else is on the track.
    /// </summary>
    [Fact]
    public async Task PrimaryArtistIsLeftAlone()
    {
        using var sp = new TestServiceProvider();
        var seeded = await SeedAsync(sp);
        await EnableAsync(sp);

        await Create(sp, new RecordingHandler(ReleaseJson())).EnrichMissingCreditsAsync();

        var track = await sp.DbContext.Tracks.FirstAsync(t => t.Id == seeded.Track.Id);
        Assert.Equal(seeded.Pouya.Id, track.ArtistId);
    }

    // ---- not doing the work twice ---------------------------------------------------------

    [Fact]
    public async Task AlreadyCreditedAlbum_IsNotFetchedAgain()
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp);
        await EnableAsync(sp);

        var handler = new RecordingHandler(ReleaseJson());
        var service = Create(sp, handler);

        Assert.Equal(1, await service.EnrichMissingCreditsAsync());
        var afterFirstRun = handler.Requests.Count;

        Assert.Equal(0, await service.EnrichMissingCreditsAsync());
        Assert.Equal(afterFirstRun, handler.Requests.Count);
    }

    /// <summary>
    /// A release MusicBrainz does not have is negative-cached, exactly like an unmatchable
    /// artist image. Without it, every run re-queries every permanently-unknown release, and
    /// each one costs a second of deliberate rate-limit delay.
    /// </summary>
    [Fact]
    public async Task UnknownRelease_IsNotRetriedOnTheNextRun()
    {
        using var sp = new TestServiceProvider();
        var seeded = await SeedAsync(sp);
        await EnableAsync(sp);

        var handler = new RecordingHandler(body: null, status: HttpStatusCode.NotFound);
        var service = Create(sp, handler);

        Assert.Equal(0, await service.EnrichMissingCreditsAsync());
        Assert.Single(handler.Requests);
        Assert.NotNull(await sp.Cache.GetAsync(MusicBrainzCreditService.MissKey(seeded.Album.Id)));

        Assert.Equal(0, await service.EnrichMissingCreditsAsync());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MalformedResponse_DoesNotThrow()
    {
        using var sp = new TestServiceProvider();
        var seeded = await SeedAsync(sp);
        await EnableAsync(sp);

        Assert.Equal(0, await Create(sp, new RecordingHandler("{ not json")).EnrichMissingCreditsAsync());
        Assert.Empty(await CreditsOf(sp, seeded.Track.Id));
    }

    // ---- operator settings ------------------------------------------------------------------

    /// <summary>
    /// MusicBrainz has no API key. A self-hosted mirror is the thing that actually lifts the
    /// rate limit, so the base URL is the setting that matters.
    /// </summary>
    [Fact]
    public async Task CustomBaseUrl_IsUsed()
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp);
        await EnableAsync(sp);
        await sp.Get<SettingsService>().SetAsync(IMusicBrainzCreditService.BaseUrlSettingKey, "http://mb.local:5000/");

        var handler = new RecordingHandler(ReleaseJson());
        await Create(sp, handler).EnrichMissingCreditsAsync();

        var url = Assert.Single(handler.Requests).Url;
        Assert.StartsWith("http://mb.local:5000/ws/2/release/", url);
        // The trailing slash on the setting must not become a double slash in the path.
        Assert.DoesNotContain("//ws", url);
    }

    [Fact]
    public async Task BlankBaseUrl_FallsBackToMusicBrainz()
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp);
        await EnableAsync(sp);
        await sp.Get<SettingsService>().SetAsync(IMusicBrainzCreditService.BaseUrlSettingKey, "   ");

        var handler = new RecordingHandler(ReleaseJson());
        await Create(sp, handler).EnrichMissingCreditsAsync();

        Assert.StartsWith(MusicBrainzCreditService.DefaultBaseUrl, Assert.Single(handler.Requests).Url);
    }

    /// <summary>MusicBrainz asks clients to identify themselves; this is where that goes.</summary>
    [Fact]
    public async Task Contact_IsAppendedToTheUserAgent()
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp);
        await EnableAsync(sp);
        await sp.Get<SettingsService>().SetAsync(IMusicBrainzCreditService.ContactSettingKey, "me@example.com");

        var handler = new RecordingHandler(ReleaseJson());
        await Create(sp, handler).EnrichMissingCreditsAsync();

        Assert.Contains("me@example.com", Assert.Single(handler.Requests).UserAgent);
    }

    /// <summary>
    /// Parentheses are User-Agent comment syntax, so an unsanitised contact produces a header
    /// the parser rejects - which would throw on the first request and abort the whole run.
    /// </summary>
    [Fact]
    public async Task ContactContainingParentheses_DoesNotBreakTheRequest()
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp);
        await EnableAsync(sp);
        await sp.Get<SettingsService>().SetAsync(
            IMusicBrainzCreditService.ContactSettingKey, "Kenny (cosine) <me@example.com>");

        var handler = new RecordingHandler(ReleaseJson());
        Assert.Equal(1, await Create(sp, handler).EnrichMissingCreditsAsync());
        Assert.Contains("me@example.com", Assert.Single(handler.Requests).UserAgent);
    }

    /// <summary>
    /// A blank or nonsense rate limit must not become a zero delay pointed at musicbrainz.org -
    /// that is how a self-hosted server gets its IP blocked.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("-5")]
    public async Task InvalidRateLimit_FallsBackToTheSafeDefault(string value)
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp);
        var settings = sp.Get<SettingsService>();
        await settings.SetAsync(IMusicBrainzCreditService.EnabledSettingKey, "true");
        await settings.SetAsync(IMusicBrainzCreditService.RateLimitSettingKey, value);

        var handler = new RecordingHandler(ReleaseJson());
        using var cts = new CancellationTokenSource();
        // The default delay is 1.1 s; cancelling well inside it proves the run is waiting rather
        // than firing immediately, without making the test wait for it.
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(sp, handler).EnrichMissingCreditsAsync(cts.Token));
        Assert.Empty(handler.Requests);
    }

    private sealed record Sent(string Url, string UserAgent);

    private sealed class RecordingHandler(string? body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Sent(
                request.RequestUri!.ToString(),
                request.Headers.UserAgent.ToString()));

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? "", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            // Mirrors the base User-Agent Program.cs configures on the real named client.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("cosine/1.0 (self-hosted music server)");
            return client;
        }
    }
}
