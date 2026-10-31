using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Hubs;
using Cosine.Infrastructure.Playback;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Loopback;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

/// <summary>
/// The advance half of <see cref="PlaybackSessionManager"/>: what a poll of a live device does to
/// the queue.
/// <para>
/// The case these exist for is the gapless join. A device that takes
/// <c>SetNextAVTransportURI</c> makes the move between tracks itself, and until it was recognised
/// the engine read the new URI as a stranger on the speaker: the session went Hijacked, stopped
/// being polled, and the queue stood still with the play button showing paused. It survived
/// twenty-six tracks on the reporter's Sonos before it happened, because the engine's
/// end-of-track tolerance usually fired first and re-set the URI itself - which is the same race
/// seen from the other side, and the reason a tolerance is no longer allowed to fire while a
/// device is holding the next track.
/// </para>
/// <para>Nothing here waits on real time: the simulated transport is stepped by hand.</para>
/// </summary>
public class PlaybackAdvanceTests
{
    private const string BaseUrl = "http://192.168.1.10:8080";

    /// <summary>
    /// Long enough that a loaded machine will not trip it, short enough that a regression fails
    /// instead of hanging. Used where the whole point of the assertion is that something returns
    /// before a deliberately-blocked resolve does.
    /// </summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>
    /// Resolves every track to a stable, distinct URL, which is what the device reports back and
    /// therefore what the engine's URI comparisons are made of.
    /// </summary>
    /// <summary>
    /// Holds one track's resolve open on demand, standing in for the wait a renderer that requires
    /// a <c>Content-Length</c> imposes: the resolver materialises the whole transcode before it can
    /// answer, which is an ffmpeg encode and takes seconds.
    /// </summary>
    private sealed class ResolverGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The one track whose resolve blocks. Everything else answers immediately.</summary>
        public Guid? Hold { get; set; }

        /// <summary>Completes once the held track has actually been asked for.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Let the encode "finish".</summary>
        public void Release() => _released.TrySetResult();

        public Task PassAsync(Guid trackId)
        {
            if (trackId != Hold) return Task.CompletedTask;
            _entered.TrySetResult();
            return _released.Task;
        }
    }

    private sealed class StubResolver(CosineDbContext db, ResolverGate? gate = null) : IPlaybackStreamResolver
    {
        public bool IsConfigured => true;

        public async Task<TrackPresentation> ResolveAsync(
            Guid sessionId, Guid trackId, Guid userId,
            PlaybackCapabilities capabilities, string? transcodeProfile, CancellationToken ct)
        {
            var track = db.Tracks.Find(trackId)
                        ?? throw new InvalidOperationException($"No track {trackId}.");

            if (gate is not null) await gate.PassAsync(trackId).WaitAsync(ct);

            return new TrackPresentation(
                TrackId: trackId,
                Url: $"{BaseUrl}/playback/stream/{trackId}",
                ContentType: "audio/mpeg",
                ContentLength: 1024,
                Duration: TimeSpan.FromMilliseconds(track.DurationMs ?? 0),
                Title: track.Title,
                Artist: "Artist",
                Album: "Album",
                CoverArtUrl: null);
        }

        public bool TryValidateToken(string token, out PlaybackStreamTicket ticket)
            => throw new NotSupportedException();

        public bool TryValidateCoverArtToken(string token, out PlaybackCoverArtTicket ticket)
            => throw new NotSupportedException();
    }

    private sealed class RecordingHistory : IPlayHistoryService
    {
        public List<Guid> Plays { get; } = [];

        public Task RecordPlayAsync(Guid userId, Guid trackId, CancellationToken ct = default)
        {
            Plays.Add(trackId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Track>> GetRecentPlaysAsync(Guid userId, int limit = 50, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<(Track Track, int Count)>> GetMostPlayedAsync(Guid userId, int limit = 50, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> CleanupAsync(TimeSpan retention, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Stands in for the similarity service auto-play calls. It answers with whatever tracks the
    /// library has that the caller does not - which is the only property the session manager
    /// depends on, and lets a test assert that the exclusion list is actually being sent.
    /// </summary>
    private sealed class StubSimilarity(CosineDbContext db) : ISimilarityService
    {
        public List<IReadOnlyList<Guid>> Seeds { get; } = [];

        public Task<IReadOnlyList<Track>> GetAutoPlayQueueAsync(
            IReadOnlyList<Guid> seedTrackIds, IReadOnlyCollection<Guid> excludeTrackIds, int count,
            CancellationToken ct = default)
        {
            Seeds.Add([.. seedTrackIds]);
            var excluded = excludeTrackIds.ToHashSet();
            IReadOnlyList<Track> batch =
                [.. db.Tracks.Where(t => !excluded.Contains(t.Id)).Take(count)];
            return Task.FromResult(batch);
        }

        public Task<IReadOnlyList<(Artist Artist, double Score)>> GetSimilarArtistsAsync(
            Guid artistId, int count, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Track>> GetArtistRadioAsync(
            Guid seedArtistId, int count, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Track>> GetTopSongsAsync(
            Guid artistId, int count, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class SilentNowPlaying : INowPlayingService
    {
        public Task ReportAsync(
            Guid userId, string username, Guid trackId,
            int? positionMs = null, string state = INowPlayingService.StatePlaying, CancellationToken ct = default)
            => Task.CompletedTask;

        public IReadOnlyList<NowPlayingReport> GetCurrent() => [];
    }

    /// <summary>
    /// A context per scope over one shared store, as the application has. Not a single shared
    /// context: arming the gapless hint resolves the next track on a scope of its own while the
    /// command that scheduled it is still finishing on another, and two threads through one
    /// <see cref="CosineDbContext"/> is a race whether or not it happens to be lost today.
    /// </summary>
    private sealed class ScopeFactory(string databaseName, IPlayHistoryService history, ResolverGate? gate)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            new Scope(TestDbContextFactory.Create(databaseName), history, gate);

        private sealed class Scope(CosineDbContext db, IPlayHistoryService history, ResolverGate? gate)
            : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) => serviceType switch
            {
                _ when serviceType == typeof(CosineDbContext) => db,
                _ when serviceType == typeof(IPlaybackStreamResolver) => new StubResolver(db, gate),
                _ when serviceType == typeof(ISimilarityService) => new StubSimilarity(db),
                _ when serviceType == typeof(IPlayHistoryService) => history,
                _ when serviceType == typeof(INowPlayingService) => new SilentNowPlaying(),
                _ => null,
            };

            public void Dispose() => db.Dispose();
        }
    }

    private sealed class SilentHubContext : IHubContext<PlaybackHub>
    {
        public IHubClients Clients { get; } = new NullClients();
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class NullClients : IHubClients
        {
            private static readonly NullProxy Proxy = new();

            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excluded) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class NullProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
                => Task.CompletedTask;
        }
    }

    /// <summary>A session on a fully capable simulated device, with a queue of real track rows.</summary>
    private sealed record Harness(
        PlaybackSessionManager Manager,
        LoopbackDriver Driver,
        LoopbackTarget Target,
        Guid SessionId,
        Guid DeviceId,
        IReadOnlyList<Guid> TrackIds,
        PlaybackCaller Caller,
        RecordingHistory History)
    {
        public PlaybackStateSnapshot State => Manager.TryGetState(SessionId)!;

        public async Task TickAsync()
        {
            await Manager.TickAsync(SessionId, default);
            await SettleAsync();
        }

        /// <summary>
        /// Wait for the gapless hint to reach the device. Arming it is deliberately off the
        /// command path - resolving the next track can be a whole transcode, and the browser used
        /// to wait out that encode before it was told what was playing - so a command returns
        /// before the device has been handed anything. Nothing in the running system cares, but an
        /// assertion about the simulated device does.
        /// </summary>
        public Task SettleAsync() => Manager.WaitForPreloadAsync(SessionId);
    }

    /// <param name="libraryExtra">
    /// Tracks that exist in the library but are not put in the queue - what auto-play has left to
    /// find. Zero for every test that is not about auto-play.
    /// </param>
    private static async Task<Harness> BuildAsync(
        int trackCount = 3, PlaybackCapabilities? capabilities = null, TimeSpan? trackLength = null,
        ResolverGate? gate = null, int libraryExtra = 0)
    {
        var databaseName = Guid.NewGuid().ToString();
        var db = TestDbContextFactory.Create(databaseName);
        var history = new RecordingHistory();
        var time = new TestTime();

        var length = trackLength ?? TimeSpan.FromMinutes(3);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);

        var trackIds = new List<Guid>();
        for (var i = 0; i < trackCount + libraryExtra; i++)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Title = $"Track {i + 1}",
                SortTitle = $"track {i + 1}",
                AlbumId = album.Id,
                ArtistId = artist.Id,
                FilePath = $"track-{i + 1}.mp3",
                DurationMs = (long)length.TotalMilliseconds,
            };
            db.Tracks.Add(track);
            trackIds.Add(track.Id);
        }

        await db.SaveChangesAsync();

        var driver = new LoopbackDriver(
            new LoopbackOptions { Capabilities = capabilities ?? LoopbackOptions.CapableDevice },
            time);

        var manager = new PlaybackSessionManager(
            new ScopeFactory(databaseName, history, gate),
            [driver],
            new SilentHubContext(),
            Options.Create(new PlaybackOptions { AdvertisedBaseUrl = BaseUrl }),
            NullLogger<PlaybackSessionManager>.Instance,
            time);

        await manager.RegisterDiscoveredAsync(driver.Devices[0], default);
        var device = (await manager.GetDevicesAsync(default))[0];

        var caller = new PlaybackCaller(Guid.NewGuid(), "listener", IsAdmin: false);
        var session = await manager.StartSessionAsync(device.Id, caller, takeOver: false, default);
        var queued = trackIds.Take(trackCount).ToList();
        await manager.SetQueueAsync(session.SessionId, caller, queued, 0, default);
        await manager.WaitForPreloadAsync(session.SessionId);

        return new Harness(
            manager, driver, driver.LastTarget!, session.SessionId, device.Id, queued, caller, history);
    }

    /// <summary>
    /// A volume ceiling has to bite now. One that waited for the next session would leave the
    /// speaker exactly as loud as the admin just said it must not be, which is the whole thing
    /// the setting is for.
    /// </summary>
    [Fact]
    public async Task LoweringADevicesVolumeCeiling_TurnsDownASessionAlreadyPlaying()
    {
        var harness = await BuildAsync();
        await harness.Manager.SetVolumeAsync(harness.SessionId, harness.Caller, 90, default);

        await harness.Manager.UpdateDeviceAsync(
            harness.DeviceId, new PlaybackDeviceSettings(MaxVolumePercent: 40), default);

        Assert.Equal(40, harness.Target.VolumePercent);
        Assert.Equal(40, harness.State.VolumePercent);

        // …and the ceiling holds against a later request to go loud again.
        await harness.Manager.SetVolumeAsync(harness.SessionId, harness.Caller, 100, default);
        Assert.Equal(40, harness.Target.VolumePercent);
    }

    /// <summary>A ceiling above where the device already is changes nothing on the device.</summary>
    [Fact]
    public async Task RaisingTheCeiling_DoesNotTouchTheCurrentVolume()
    {
        var harness = await BuildAsync();
        await harness.Manager.SetVolumeAsync(harness.SessionId, harness.Caller, 30, default);

        await harness.Manager.UpdateDeviceAsync(
            harness.DeviceId, new PlaybackDeviceSettings(MaxVolumePercent: 80), default);

        Assert.Equal(30, harness.Target.VolumePercent);
    }

    /// <summary>
    /// The reported bug, reduced: the device makes the join itself onto the URI cosine gave it as
    /// the next track. That is the preload doing its job, and the session must follow it - not
    /// decide a stranger has taken the speaker.
    /// </summary>
    [Fact]
    public async Task ADeviceThatMakesTheGaplessJoinItself_IsFollowed_NotTreatedAsAHijack()
    {
        var harness = await BuildAsync();

        await harness.TickAsync();               // the device confirms it is playing track 1
        harness.Target.SimulateGaplessAdvance(); // …and moves onto the preloaded track 2 on its own
        await harness.TickAsync();

        var state = harness.State;
        Assert.Equal(PlaybackSessionState.Playing, state.State);
        Assert.Equal(1, state.QueueIndex);
        Assert.Equal(harness.TrackIds[1], state.CurrentTrackId);
    }

    /// <summary>
    /// And the join is followed without commanding the device: re-issuing <c>SetAVTransportURI</c>
    /// for a track already playing would restart it audibly, which is the opposite of gapless.
    /// </summary>
    [Fact]
    public async Task FollowingAGaplessJoin_DoesNotReloadTheTrackTheDeviceIsAlreadyPlaying()
    {
        var harness = await BuildAsync();
        await harness.TickAsync();

        var loadsBefore = harness.Target.Commands.Count(c => c.Kind == LoopbackCommandKind.Load);
        harness.Target.SimulateGaplessAdvance();
        await harness.TickAsync();

        Assert.Equal(loadsBefore, harness.Target.Commands.Count(c => c.Kind == LoopbackCommandKind.Load));
        Assert.Equal(harness.TrackIds[1], harness.Target.LoadedTrack!.TrackId);
    }

    /// <summary>The queue keeps moving afterwards: the next hint is re-armed as part of following.</summary>
    [Fact]
    public async Task AfterAGaplessJoin_TheFollowingTrackIsPreloaded()
    {
        var harness = await BuildAsync();
        await harness.TickAsync();

        harness.Target.SimulateGaplessAdvance();
        await harness.TickAsync();

        Assert.Equal(harness.TrackIds[2], harness.Target.PreloadedTrack!.TrackId);

        // …and it happens again, all the way to the end of the queue.
        harness.Target.SimulateGaplessAdvance();
        await harness.TickAsync();

        Assert.Equal(2, harness.State.QueueIndex);
        Assert.Null(harness.Target.PreloadedTrack);
    }

    /// <summary>
    /// The reported lag, reduced: the browser showed the old track for about five seconds after a
    /// speaker had audibly moved on to the new one, and only where the device was being sent a
    /// transcode.
    /// <para>
    /// What took those seconds was resolving the track <em>after</em> the one that had just
    /// started: on a renderer that requires a Content-Length the resolver materialises a whole
    /// transcode before it can give one, and that ran inline, before the poll published anything.
    /// The audio was never waiting on it - the device had made the gapless join itself - only the
    /// UI was. So the state has to go out first, and the hint follows when it is ready.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AGaplessJoin_IsPublished_WithoutWaitingForTheFollowingTrackToResolve()
    {
        var gate = new ResolverGate();
        var harness = await BuildAsync(gate: gate);
        await harness.TickAsync();

        // Track 3 is what gets armed once the device joins onto track 2, so it is the resolve that
        // used to be in the way.
        gate.Hold = harness.TrackIds[2];
        harness.Target.SimulateGaplessAdvance();

        // Deliberately not harness.TickAsync(), which waits for the arming this test is about.
        await harness.Manager.TickAsync(harness.SessionId, default).WaitAsync(Promptly);
        await gate.Entered.WaitAsync(Promptly);

        // The tick has returned and the session is on track 2 - with track 3 still encoding.
        Assert.Equal(1, harness.State.QueueIndex);
        Assert.Equal(harness.TrackIds[1], harness.State.CurrentTrackId);
        Assert.Equal(PlaybackSessionState.Playing, harness.State.State);
        Assert.Null(harness.Target.PreloadedTrack);

        // And the hint still lands, once it can.
        gate.Release();
        await harness.SettleAsync();
        Assert.Equal(harness.TrackIds[2], harness.Target.PreloadedTrack!.TrackId);
    }

    /// <summary>
    /// The same for a button: <c>next</c> answers as soon as the device is playing, not once the
    /// track after it has been encoded. The browser applies the command's own response, so a
    /// response held open is a UI that has not moved.
    /// </summary>
    [Fact]
    public async Task Next_AnswersBeforeTheTrackAfterItHasBeenResolved()
    {
        var gate = new ResolverGate();
        var harness = await BuildAsync(gate: gate);
        gate.Hold = harness.TrackIds[2];

        var state = await harness.Manager
            .NextAsync(harness.SessionId, harness.Caller, default)
            .WaitAsync(Promptly);
        await gate.Entered.WaitAsync(Promptly);

        Assert.Equal(1, state.QueueIndex);
        Assert.Equal(harness.TrackIds[1], state.CurrentTrackId);
        Assert.Equal(harness.TrackIds[1], harness.Target.LoadedTrack!.TrackId);

        gate.Release();
        await harness.SettleAsync();
        Assert.Equal(harness.TrackIds[2], harness.Target.PreloadedTrack!.TrackId);
    }

    /// <summary>
    /// A real hijack still is one. The preload is the only foreign URI that means something
    /// friendly, so widening the check must not have widened it to everything.
    /// </summary>
    [Fact]
    public async Task AUriCosineNeverSet_IsStillAHijack()
    {
        var harness = await BuildAsync();
        await harness.TickAsync();

        harness.Target.SimulateHijack("x-sonos-spotify:something-else");
        await harness.TickAsync();

        Assert.Equal(PlaybackSessionState.Hijacked, harness.State.State);
    }

    /// <summary>
    /// The other half of the race. While the device is holding the next track, the end-of-track
    /// tolerance must not fire: it would cut the last second off the track and hand the device a
    /// URI it was about to load itself.
    /// </summary>
    [Fact]
    public async Task NearTheEndOfATrack_ADeviceHoldingTheNextOne_IsLeftToMakeTheJoin()
    {
        var harness = await BuildAsync(trackLength: TimeSpan.FromSeconds(10));
        await harness.TickAsync();

        // Sit the transport a hair short of the end - inside EndOfTrackToleranceMs.
        await harness.Manager.SeekAsync(
            harness.SessionId, harness.Caller, TimeSpan.FromMilliseconds(9_800), default);
        await harness.TickAsync();

        Assert.Equal(0, harness.State.QueueIndex);
        Assert.Equal(harness.TrackIds[1], harness.Target.PreloadedTrack!.TrackId);
    }

    /// <summary>
    /// A device that cannot preload has nothing to wait for, so the tolerance is still what ends
    /// the track - that is the whole reason it exists.
    /// </summary>
    [Fact]
    public async Task NearTheEndOfATrack_ADeviceWithNoPreload_IsAdvancedByTheServer()
    {
        var harness = await BuildAsync(
            capabilities: LoopbackOptions.CapableDevice with { SupportsPreload = false },
            trackLength: TimeSpan.FromSeconds(10));

        await harness.TickAsync();
        await harness.Manager.SeekAsync(
            harness.SessionId, harness.Caller, TimeSpan.FromMilliseconds(9_800), default);
        await harness.TickAsync();

        Assert.Equal(1, harness.State.QueueIndex);
        Assert.Equal(PlaybackSessionState.Playing, harness.State.State);
    }

    /// <summary>
    /// Repeat-one must not arm a hint at all: the device acts on the next URI by itself, so
    /// hinting the following track is telling it to do the opposite of the mode.
    /// </summary>
    [Fact]
    public async Task UnderRepeatOne_NoNextTrackIsHandedToTheDevice()
    {
        var harness = await BuildAsync();
        Assert.NotNull(harness.Target.PreloadedTrack);

        await harness.Manager.SetRepeatAsync(
            harness.SessionId, harness.Caller, PlaybackRepeatMode.One, default);
        await harness.SettleAsync();

        Assert.Null(harness.Target.PreloadedTrack);
    }

    /// <summary>
    /// Appending to a queue whose last track is playing - what an endless radio does - has to
    /// reach the device, because on a gapless renderer the hint is what actually plays next.
    /// </summary>
    [Fact]
    public async Task AppendingToTheQueue_ArmsTheDeviceWithTheTrackThatIsNowNext()
    {
        var harness = await BuildAsync(trackCount: 1);
        Assert.Null(harness.Target.PreloadedTrack);

        var db = TestDbContextFactory.Create();
        var extra = harness.TrackIds[0];

        // Re-adding the same row is enough: what matters is that the hint stops being empty.
        await harness.Manager.AddToQueueAsync(harness.SessionId, harness.Caller, [extra], null, default);
        await harness.SettleAsync();

        Assert.NotNull(harness.Target.PreloadedTrack);
        Assert.Equal(extra, harness.Target.PreloadedTrack!.TrackId);
    }

    // ---- Auto-play ----
    //
    // The point of putting this on the session rather than in the browser: a speaker keeps playing
    // after the tab that started it is closed, and nothing in these tests is a client.

    /// <summary>
    /// Turning it on with a queue that is already short fills it there and then. Waiting for the
    /// current track to end would mean switching it on during the last track of an album did
    /// nothing observable until the album was over.
    /// </summary>
    [Fact]
    public async Task EnablingAutoPlay_ExtendsAQueueThatIsAlreadyNearlyDone()
    {
        var harness = await BuildAsync(trackCount: 2, libraryExtra: 5);
        var before = harness.Manager.TryGetQueue(harness.SessionId)!.Items.Count;

        var state = await harness.Manager.SetAutoPlayAsync(
            harness.SessionId, harness.Caller, true, default);
        await harness.SettleAsync();

        Assert.True(state.AutoPlay);
        Assert.True(harness.Manager.TryGetQueue(harness.SessionId)!.Items.Count > before);
    }

    /// <summary>
    /// What the added entries are for the queue panel: the listener's own queue ended somewhere,
    /// and only the server knows where when a speaker is the output.
    /// </summary>
    [Fact]
    public async Task AutoPlayAddedEntries_AreMarkedInTheQueue()
    {
        var harness = await BuildAsync(trackCount: 2, libraryExtra: 5);
        await harness.Manager.SetAutoPlayAsync(harness.SessionId, harness.Caller, true, default);
        await harness.SettleAsync();

        var items = harness.Manager.TryGetQueue(harness.SessionId)!.Items;

        Assert.All(items.Take(2), i => Assert.False(i.AutoPlay));
        Assert.Contains(items, i => i.AutoPlay);
    }

    /// <summary>
    /// A queue with plenty left in it is left alone; the top-up waits until the end is in sight.
    /// Otherwise switching auto-play on would append twenty tracks to an album someone had just
    /// started.
    /// </summary>
    [Fact]
    public async Task AutoPlay_WaitsUntilTheQueueIsNearlyDone()
    {
        var harness = await BuildAsync(trackCount: 6, libraryExtra: 4);
        await harness.Manager.SetAutoPlayAsync(harness.SessionId, harness.Caller, true, default);
        await harness.SettleAsync();

        Assert.Equal(6, harness.Manager.TryGetQueue(harness.SessionId)!.Items.Count);

        // Skipping toward the end brings it within the threshold, and the skip that does is the
        // one that fetches - a skip is an advance like any other.
        for (var i = 0; i < 3; i++)
            await harness.Manager.NextAsync(harness.SessionId, harness.Caller, default);
        await harness.SettleAsync();

        Assert.True(harness.Manager.TryGetQueue(harness.SessionId)!.Items.Count > 6);
    }

    /// <summary>
    /// The feature, end to end: the device reaches the end of the last track and the session
    /// carries on instead of stopping. Ordering is the whole test - a top-up that ran after the
    /// advance would be answering a queue that had already returned Stop and a device that had
    /// already been told to stop.
    /// </summary>
    [Fact]
    public async Task WithAutoPlayOn_TheLastTrackFinishing_DoesNotEndTheSession()
    {
        var harness = await BuildAsync(
            trackCount: 6, libraryExtra: 4,
            capabilities: LoopbackOptions.CapableDevice with { SupportsPreload = false },
            trackLength: TimeSpan.FromSeconds(10));

        await harness.Manager.SetAutoPlayAsync(harness.SessionId, harness.Caller, true, default);
        // Straight to the last entry, without going through an advance that would top up first.
        await harness.Manager.PlayAtAsync(harness.SessionId, harness.Caller, 5, default);
        Assert.Equal(6, harness.Manager.TryGetQueue(harness.SessionId)!.Items.Count);

        await harness.TickAsync();
        await harness.Manager.SeekAsync(
            harness.SessionId, harness.Caller, TimeSpan.FromMilliseconds(9_800), default);
        await harness.TickAsync();

        Assert.Equal(PlaybackSessionState.Playing, harness.State.State);
        Assert.Equal(6, harness.State.QueueIndex);
        Assert.True(harness.Manager.TryGetQueue(harness.SessionId)!.Items.Count > 6);
    }

    /// <summary>Off is off: the queue still ends where it always did.</summary>
    [Fact]
    public async Task WithAutoPlayOff_TheQueueStillEnds()
    {
        var harness = await BuildAsync(
            trackCount: 1, libraryExtra: 4,
            capabilities: LoopbackOptions.CapableDevice with { SupportsPreload = false },
            trackLength: TimeSpan.FromSeconds(10));

        await harness.TickAsync();
        await harness.Manager.SeekAsync(
            harness.SessionId, harness.Caller, TimeSpan.FromMilliseconds(9_800), default);
        await harness.TickAsync();

        Assert.Equal(PlaybackSessionState.Stopped, harness.State.State);
    }

    /// <summary>
    /// The batch is seeded from what has actually been played, and asked for without it: a radio
    /// that circles back to the track from twenty minutes ago is the failure this feature is
    /// judged on, so the exclusion list has to carry the queue and the history both.
    /// </summary>
    [Fact]
    public async Task AutoPlay_DoesNotQueueAnythingAlreadyInTheQueue()
    {
        var harness = await BuildAsync(trackCount: 2, libraryExtra: 6);
        await harness.Manager.SetAutoPlayAsync(harness.SessionId, harness.Caller, true, default);
        await harness.SettleAsync();

        var ids = harness.Manager.TryGetQueue(harness.SessionId)!.Items.Select(i => i.TrackId).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// Repeat already means the queue never ends. Topping it up as well would grow it forever
    /// while it loops over the part that was there first.
    /// </summary>
    [Fact]
    public async Task UnderRepeat_AutoPlayAddsNothing()
    {
        var harness = await BuildAsync(trackCount: 2, libraryExtra: 5);
        await harness.Manager.SetRepeatAsync(
            harness.SessionId, harness.Caller, PlaybackRepeatMode.All, default);

        await harness.Manager.SetAutoPlayAsync(harness.SessionId, harness.Caller, true, default);
        await harness.SettleAsync();

        Assert.Equal(2, harness.Manager.TryGetQueue(harness.SessionId)!.Items.Count);
    }
}
