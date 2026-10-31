using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Playback;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Loopback;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Cosine.Tests.Integration;

/// <summary>
/// The plan's Phase 1 acceptance test (§5): a Loopback session auto-advances through three
/// tracks with correct state on the hub, and a minted token fetches real audio while an expired
/// one 404s.
/// <para>
/// This boots the real application through <c>WebApplicationFactory</c> rather than hand-wiring
/// a <c>ServiceCollection</c> like the older integration tests. The endpoint under test is
/// anonymous, route-matched and range-serving, and <c>MapFallbackToFile</c> answers unmatched
/// paths with 200 and the SPA shell - none of which a service-level test would catch.
/// </para>
/// </summary>
public sealed class PlaybackIntegrationTests : IAsyncLifetime
{
    private const string Password = "playback-test-password";

    /// <summary>
    /// Fast enough that three tracks finish in well under a second, slow enough that the poll
    /// loop still sees each of them play. The tracks are 4 s of simulated audio at 40× real time.
    /// </summary>
    private const double ClockRate = 40.0;
    private static readonly TimeSpan TrackDuration = TimeSpan.FromSeconds(4);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cosine_playback_test")
        .WithUsername("cosine")
        .WithPassword("cosine_test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();

    private CosineWebApplicationFactory _factory = null!;
    private string _mediaRoot = null!;
    private Guid _userId;
    private Guid _deviceId;
    private List<Guid> _trackIds = null!;
    private string _authCookie = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        _mediaRoot = Path.Combine(Path.GetTempPath(), $"cosine-playback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mediaRoot);
        // Real bytes, so the stream endpoint is serving an actual audio file rather than a stub.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"),
            Path.Combine(_mediaRoot, "track.flac"));

        _factory = new CosineWebApplicationFactory(
            _postgres.GetConnectionString(), _redis.GetConnectionString(), _mediaRoot);

        // Force the host to build before seeding, so migrations have run.
        using (var client = _factory.CreateClient()) { }

        await SeedAsync();
        _authCookie = await LoginAsync();
        await WaitForDiscoveryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        if (Directory.Exists(_mediaRoot)) Directory.Delete(_mediaRoot, recursive: true);
    }

    // ---- The acceptance test ----

    [Fact]
    public async Task LoopbackSession_AutoAdvancesThroughThreeTracks_PublishingStateOnTheHub()
    {
        await using var hub = await ConnectHubAsync();
        var states = new List<PlaybackStateSnapshot>();
        hub.On<PlaybackStateSnapshot>("PlaybackState", s => { lock (states) states.Add(s); });
        var queues = new List<PlaybackQueueSnapshot>();
        hub.On<PlaybackQueueSnapshot>("QueueChanged", q => { lock (queues) queues.Add(q); });

        var manager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        var caller = new PlaybackCaller(_userId, "playback-tester", IsAdmin: false);

        var session = await manager.StartSessionAsync(_deviceId, caller, takeOver: false, CancellationToken.None);
        await hub.InvokeAsync("Subscribe", session.SessionId);

        await manager.SetQueueAsync(session.SessionId, caller, _trackIds, startIndex: 0, CancellationToken.None);

        // The engine advances the queue by polling the device; nothing here tells it to.
        var reachedEnd = await WaitUntilAsync(() =>
            manager.TryGetState(session.SessionId) is { State: PlaybackSessionState.Stopped },
            TimeSpan.FromSeconds(30));

        var final = manager.TryGetState(session.SessionId);
        Assert.True(reachedEnd,
            $"Session never reached the end of the queue. Final state: {final?.State}, index {final?.QueueIndex}.");

        // Every track played, in order, and the queue stopped rather than wrapping.
        Assert.Equal(2, final!.QueueIndex);
        Assert.Equal(PlaybackSessionState.Stopped, final.State);

        var loopback = _factory.Loopback.LastTarget!;
        var loaded = loopback.Commands
            .Where(c => c.Kind == LoopbackCommandKind.Load)
            .Select(c => c.Track!.TrackId)
            .ToList();
        Assert.Equal(_trackIds, loaded);

        // Give the last hub messages a moment to land before asserting on them.
        await WaitUntilAsync(() =>
        {
            lock (states) return states.Any(s => s.State == PlaybackSessionState.Stopped);
        }, TimeSpan.FromSeconds(5));

        List<PlaybackStateSnapshot> observed;
        lock (states) observed = [.. states];

        Assert.NotEmpty(observed);
        Assert.All(observed, s => Assert.Equal(session.SessionId, s.SessionId));

        // The hub saw each track become current, in queue order.
        var currentTracks = observed
            .Select(s => s.CurrentTrackId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        Assert.Equal(_trackIds, currentTracks);

        Assert.Contains(observed, s => s.State == PlaybackSessionState.Playing);
        Assert.Contains(observed, s => s.State == PlaybackSessionState.Stopped);

        // The queue travelled as its own event, not folded into the per-second state messages.
        List<PlaybackQueueSnapshot> queueEvents;
        lock (queues) queueEvents = [.. queues];
        var queueEvent = Assert.Single(queueEvents);
        Assert.Equal(_trackIds, queueEvent.Items.Select(i => i.TrackId));
        Assert.Equal(Enumerable.Range(0, 3), queueEvent.Items.Select(i => i.Position));
        Assert.All(queueEvent.Items, i => Assert.NotEqual(string.Empty, i.Title));

        await manager.EndSessionAsync(session.SessionId, caller, CancellationToken.None);
    }

    /// <summary>
    /// Scrobbling for a remote session is the poll loop's job (plan §5 Phase 3): the browser's
    /// <c>maybeScrobble</c> hangs off <c>timeupdate</c>, and there is no <c>timeupdate</c> when the
    /// audio never reaches the browser. What matters is that it counts a play *once* - the loop
    /// runs a hundred times over these three tracks, and a play count that follows the poll
    /// interval rather than the listening would be worse than none at all.
    /// </summary>
    [Fact]
    public async Task PollLoop_RecordsExactlyOnePlayPerTrack_ForARemoteSession()
    {
        var manager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        var caller = new PlaybackCaller(_userId, "playback-tester", IsAdmin: false);
        var session = await manager.StartSessionAsync(_deviceId, caller, takeOver: false, CancellationToken.None);

        await manager.SetQueueAsync(session.SessionId, caller, _trackIds, startIndex: 0, CancellationToken.None);

        // Ticked here rather than left to the engine's background loop. These tracks are four
        // simulated seconds at 40× real time, so the whole queue is over in a few hundred
        // milliseconds and the halfway point of each track is a ~50 ms window - dense enough
        // sampling is what makes this a test of the rule rather than of the scheduler. Real
        // hardware gets a hundred polls per track and no such difficulty.
        var finished = await WaitUntilAsync(async () =>
        {
            await manager.TickAsync(session.SessionId, CancellationToken.None);
            await Task.Delay(5);
            return manager.TryGetState(session.SessionId) is { State: PlaybackSessionState.Stopped };
        }, TimeSpan.FromSeconds(30));
        Assert.True(finished, "The session never reached the end of the queue.");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        var plays = await db.PlayHistory
            .Where(p => p.UserId == _userId)
            .Select(p => p.TrackId)
            .ToListAsync();

        Assert.Equal(3, plays.Count);
        Assert.Equal(_trackIds.OrderBy(id => id), plays.OrderBy(id => id));

        // The same submission also reports now-playing, exactly as /rest/scrobble does - otherwise
        // a listener on a speaker would be invisible to everyone else in the house.
        var nowPlaying = _factory.Services.GetRequiredService<INowPlayingService>().GetCurrent();
        Assert.Contains(nowPlaying, r => r.UserId == _userId && _trackIds.Contains(r.TrackId));

        await manager.EndSessionAsync(session.SessionId, caller, CancellationToken.None);
    }

    [Fact]
    public async Task MintedToken_FetchesRealAudio_AndAnExpiredOneIs404()
    {
        var manager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        var caller = new PlaybackCaller(_userId, "playback-tester", IsAdmin: false);
        var session = await manager.StartSessionAsync(_deviceId, caller, takeOver: false, CancellationToken.None);

        try
        {
            using var scope = _factory.Services.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IPlaybackStreamResolver>();

            var presentation = await resolver.ResolveAsync(
                session.SessionId, _trackIds[0], _userId,
                LoopbackOptions.CapableDevice, null, CancellationToken.None);

            // The URL is absolute and built from AdvertisedBaseUrl, not the request host - a
            // speaker has no route to the browser's hostname.
            Assert.StartsWith("http://192.168.1.10:8080/playback/stream/", presentation.Url);

            using var client = _factory.CreateClient();
            var path = new Uri(presentation.Url).PathAndQuery;

            // No cookie, no credential of any kind: this is exactly what a renderer sends.
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var expectedBytes = await File.ReadAllBytesAsync(Path.Combine(_mediaRoot, "track.flac"));
            var served = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(expectedBytes.Length, served.Length);
            Assert.Equal(expectedBytes, served);
            Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expectedBytes.LongLength, response.Content.Headers.ContentLength);

            // Renderers seek with Range and probe with HEAD; both must work.
            Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
            Assert.Contains("DLNA.ORG_OP=01", response.Headers.GetValues("contentFeatures.dlna.org").Single());

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, path);
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
            var ranged = await client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal(100, (await ranged.Content.ReadAsByteArrayAsync()).Length);

            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);

            // An expired token 404s - not 401, so a probe cannot tell expiry from nonexistence.
            var expired = await client.GetAsync($"/playback/stream/{ExpiredToken(session.SessionId)}");
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);

            // Garbage 404s too, and is genuinely routed - MapFallbackToFile would answer 200.
            var garbage = await client.GetAsync("/playback/stream/not-a-real-token");
            Assert.Equal(HttpStatusCode.NotFound, garbage.StatusCode);
        }
        finally
        {
            await manager.EndSessionAsync(session.SessionId, caller, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Token_StopsWorking_WhenTheSessionEnds()
    {
        // Ending a session is the revocation mechanism - the tokens are deliberately not
        // one-shot, so nothing else would invalidate them.
        var manager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        var caller = new PlaybackCaller(_userId, "playback-tester", IsAdmin: false);
        var session = await manager.StartSessionAsync(_deviceId, caller, takeOver: false, CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPlaybackStreamResolver>();
        var presentation = await resolver.ResolveAsync(
            session.SessionId, _trackIds[0], _userId,
            LoopbackOptions.CapableDevice, null, CancellationToken.None);
        var path = new Uri(presentation.Url).PathAndQuery;

        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);

        await manager.EndSessionAsync(session.SessionId, caller, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Devices_AreListedOverHttp_AndASecondUserCannotStealABusyDevice()
    {
        var manager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        var owner = new PlaybackCaller(_userId, "playback-tester", IsAdmin: false);
        var session = await manager.StartSessionAsync(_deviceId, owner, takeOver: false, CancellationToken.None);

        try
        {
            using var client = AuthenticatedClient();
            var devices = await client.GetFromJsonAsync<List<PlaybackDeviceInfo>>("/playback/devices");
            var device = Assert.Single(devices!);
            Assert.Equal(_deviceId, device.Id);
            Assert.True(device.Online);
            Assert.Equal(session.SessionId, device.ActiveSessionId);
            Assert.Equal("playback-tester", device.ActiveSessionOwner);

            var intruder = new PlaybackCaller(Guid.NewGuid(), "someone-else", IsAdmin: false);

            // Asking politely and asking rudely both fail: only an admin may take a busy device.
            await Assert.ThrowsAsync<PlaybackDeviceBusyException>(
                () => manager.StartSessionAsync(_deviceId, intruder, takeOver: false, CancellationToken.None));
            await Assert.ThrowsAsync<PlaybackDeviceBusyException>(
                () => manager.StartSessionAsync(_deviceId, intruder, takeOver: true, CancellationToken.None));

            // The original session is untouched.
            Assert.NotNull(manager.TryGetState(session.SessionId));
        }
        finally
        {
            if (manager.IsSessionLive(session.SessionId))
                await manager.EndSessionAsync(session.SessionId, owner, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnAdmin_CanTakeOverABusyDevice()
    {
        var manager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        var owner = new PlaybackCaller(_userId, "playback-tester", IsAdmin: false);
        var original = await manager.StartSessionAsync(_deviceId, owner, takeOver: false, CancellationToken.None);

        var admin = new PlaybackCaller(await SeedUserAsync("playback-admin"), "playback-admin", IsAdmin: true);
        var taken = await manager.StartSessionAsync(_deviceId, admin, takeOver: true, CancellationToken.None);

        try
        {
            Assert.NotEqual(original.SessionId, taken.SessionId);
            Assert.False(manager.IsSessionLive(original.SessionId));
            Assert.Equal(admin.UserId, taken.OwnerUserId);
        }
        finally
        {
            await manager.EndSessionAsync(taken.SessionId, admin, CancellationToken.None);
        }
    }

    /// <summary>
    /// The two API surfaces authenticate exclusively: a JWT cookie never authenticates
    /// <c>/rest</c> (so a CSRF'd browser request carries nothing Subsonic will honour), while
    /// Subsonic token auth works there and the cookie works on the web surface. This is the
    /// end state of the /rest–/api split; a regression here quietly reopens cookie-authed
    /// Subsonic writes.
    /// </summary>
    [Fact]
    public async Task AuthSurfaces_AreSeparate()
    {
        // Cookie on /rest: Subsonic reports missing credentials as HTTP 200 + error 40.
        using var cookieClient = AuthenticatedClient();
        var denied = await cookieClient.GetStringAsync("/rest/ping?f=json&c=test&v=1.16.1");
        Assert.Contains("\"status\":\"failed\"", denied);
        Assert.Contains("40", denied);

        // Subsonic token auth (md5(secret + salt)) authenticates /rest.
        string secret;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            secret = (await db.Users.SingleAsync(u => u.Username == "playback-tester")).SubsonicSecret;
        }
        const string salt = "c0s1ne";
        var token = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret + salt))).ToLowerInvariant();
        using var subsonicClient = _factory.CreateClient();
        var allowed = await subsonicClient.GetStringAsync(
            $"/rest/ping?u=playback-tester&t={token}&s={salt}&v=1.16.1&c=test&f=json");
        Assert.Contains("\"status\":\"ok\"", allowed);

        // The same cookie authenticates the web surface.
        Assert.Equal(HttpStatusCode.OK, (await cookieClient.GetAsync("/auth/me")).StatusCode);
    }

    // ---- Setup ----

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        _userId = await SeedUserAsync("playback-tester");

        var artist = new Artist { Id = Guid.NewGuid(), Name = "Test Artist", SortName = "Test Artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Test Album", SortName = "Test Album", ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);

        _trackIds = [];
        for (var i = 0; i < 3; i++)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Title = $"Track {i + 1}",
                SortTitle = $"Track {i + 1}",
                AlbumId = album.Id,
                ArtistId = artist.Id,
                TrackNumber = i + 1,
                DurationMs = (long)TrackDuration.TotalMilliseconds,
                FilePath = "track.flac",
                Format = "flac",
                ContentType = "audio/flac",
                FileSize = new FileInfo(Path.Combine(_mediaRoot, "track.flac")).Length,
            };
            db.Tracks.Add(track);
            _trackIds.Add(track.Id);
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedUserAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Cosine.Core.Interfaces.IUserService>();
        var user = await users.CreateUserAsync(username, Password);
        return user.Id;
    }

    /// <summary>
    /// The Loopback driver's discovery runs in the engine's background loop, so the device row
    /// appears shortly after startup rather than synchronously.
    /// </summary>
    private async Task WaitForDiscoveryAsync()
    {
        var manager = _factory.Services.GetRequiredService<IPlaybackSessionManager>();
        var found = await WaitUntilAsync(async () =>
        {
            var devices = await manager.GetDevicesAsync(CancellationToken.None);
            if (devices.Count == 0) return false;
            _deviceId = devices[0].Id;
            return true;
        }, TimeSpan.FromSeconds(30));

        Assert.True(found, "The Loopback driver was never discovered.");
    }

    private async Task<string> LoginAsync()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        // The CSRF gate on cookie surfaces wants proof of same-origin script; a non-browser
        // client opts in with this header, exactly as the middleware documents.
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.PostAsJsonAsync("/auth/login",
            new { Username = "playback-tester", Password });
        response.EnsureSuccessStatusCode();

        var cookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("cosine_auth=", StringComparison.Ordinal));
        return cookie.Split(';')[0];
    }

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", _authCookie);
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    private async Task<HubConnection> ConnectHubAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/playback"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                // The hub authenticates from the cosine_auth cookie; there is no
                // access_token query-string branch in this app.
                o.Headers["Cookie"] = _authCookie;
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>
    /// Polls rather than sleeping a fixed interval, so the test is neither flaky on a slow
    /// machine nor slower than it needs to be on a fast one.
    /// </summary>
    private static Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(25);
        }

        return await condition();
    }

    /// <summary>
    /// Mints a token that expired an hour ago, using the application's own key ring - the only
    /// honest way to produce one without waiting out the real TTL.
    /// </summary>
    private string ExpiredToken(Guid sessionId)
    {
        var provider = _factory.Services
            .GetRequiredService<IDataProtectionProvider>();
        var protector = provider.CreateProtector("cosine.playback.stream")
            .ToTimeLimitedDataProtector();

        var payload = JsonSerializer.Serialize(
            new PlaybackStreamTicket(sessionId, _trackIds[0], _userId, null, null));
        return protector.Protect(payload, DateTimeOffset.UtcNow.AddHours(-1));
    }

    /// <summary>
    /// The real application, with the Loopback driver registered. Loopback reaches production DI
    /// nowhere - this is the only place it is wired in, which is what makes "drivers live outside
    /// Infrastructure" a rule rather than a habit (plan §4.6).
    /// </summary>
    private sealed class CosineWebApplicationFactory(
        string postgresConnectionString, string redisConnectionString, string mediaRoot)
        : WebApplicationFactory<Program>
    {
        public LoopbackDriver Loopback { get; } = new(new LoopbackOptions
        {
            DeviceNames = ["Loopback Speaker"],
            ClockRate = ClockRate,
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:cosine", postgresConnectionString);
            builder.UseSetting("ConnectionStrings:redis", redisConnectionString);
            builder.UseSetting("Media:RootPath", mediaRoot);
            builder.UseSetting("Transcoding:CachePath", Path.Combine(mediaRoot, ".transcode-cache"));
            builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)));
            builder.UseSetting("Jwt:Issuer", "Cosine");
            builder.UseSetting("Jwt:Audience", "Cosine");

            // An address a speaker could reach. Nothing dials it in this test - what matters is
            // that the resolver builds from it rather than from the request host.
            builder.UseSetting("Playback:AdvertisedBaseUrl", "http://192.168.1.10:8080");
            builder.UseSetting("Playback:PollIntervalMs", "40");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Loopback);
                services.AddSingleton<IPlaybackDriver>(sp => sp.GetRequiredService<LoopbackDriver>());
            });
        }
    }
}
