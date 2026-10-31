using System.IO;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Loopback;

namespace Cosine.Tests.Unit;

/// <summary>
/// The Loopback driver is the fake device the playback engine is tested against (plan §4.6), so
/// it has to be trustworthy itself: a simulated clock that lies about the position, or an
/// end-of-track that never arrives, would make every engine test meaningless.
/// <para>
/// Nothing here waits on real time. The target's clock is a <see cref="TimeProvider"/>, so these
/// tests move time by assignment.
/// </para>
/// </summary>
public class LoopbackDriverTests
{
    /// <summary>
    /// A clock a test drives by hand. Overrides the timestamp members too, not just
    /// <see cref="GetUtcNow"/> - the target measures elapsed time with
    /// <see cref="TimeProvider.GetElapsedTime(long, long)"/>, which the base class would
    /// otherwise satisfy from a real <see cref="System.Diagnostics.Stopwatch"/>.
    /// </summary>
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _now.UtcTicks;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static TrackPresentation Track(int seconds = 180, string url = "http://cosine.local/playback/stream/tok-1")
        => new(
            TrackId: Guid.NewGuid(),
            Url: url,
            ContentType: "audio/flac",
            ContentLength: 1234,
            Duration: TimeSpan.FromSeconds(seconds),
            Title: "Title",
            Artist: "Artist",
            Album: "Album",
            CoverArtUrl: null);

    private static (LoopbackDriver Driver, TestTime Time) Driver(LoopbackOptions? options = null)
    {
        var time = new TestTime();
        return (new LoopbackDriver(options, time), time);
    }

    private static async Task<(LoopbackTarget Target, TestTime Time)> ConnectAsync(LoopbackOptions? options = null)
    {
        var (driver, time) = Driver(options);
        var target = (LoopbackTarget)await driver.ConnectAsync(driver.Devices[0], CancellationToken.None);
        return (target, time);
    }

    // ---- position and the simulated clock ---------------------------------------------------

    [Fact]
    public async Task Position_AdvancesOnlyWhilePlaying()
    {
        var (target, time) = await ConnectAsync();

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);

        // Loaded but not started: the clock runs, the transport does not.
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, target.Position);
        Assert.Equal(PlaybackState.Stopped, target.State);

        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));

        var playing = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Playing, playing.State);
        Assert.Equal(TimeSpan.FromSeconds(30), playing.Position);

        // Paused: frozen again, no matter how much time passes.
        await target.PauseAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));

        var paused = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Paused, paused.State);
        Assert.Equal(TimeSpan.FromSeconds(30), paused.Position);
    }

    [Fact]
    public async Task PauseThenPlay_ResumesFromWhereItStopped()
    {
        var (target, time) = await ConnectAsync();

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(20));
        await target.PauseAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(45));
        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(5));

        var state = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Playing, state.State);
        Assert.Equal(TimeSpan.FromSeconds(25), state.Position);
    }

    [Fact]
    public async Task ClockRate_ScalesHowFastTheSimulatedTransportRuns()
    {
        var options = new LoopbackOptions { ClockRate = 60.0 };
        var (target, time) = await ConnectAsync(options);

        await target.LoadAsync(Track(seconds: 180), TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));

        // One real second is a minute of a 60x device.
        Assert.Equal(TimeSpan.FromSeconds(60), target.Position);
    }

    [Fact]
    public void ClockRate_MustBePositiveAndFinite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackDriver(new LoopbackOptions { ClockRate = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoopbackDriver(new LoopbackOptions { ClockRate = double.PositiveInfinity }));
    }

    // ---- end of track ------------------------------------------------------------------------

    [Fact]
    public async Task Poll_ReportsStoppedAtEndOfTrack_AndNeverRunsPastIt()
    {
        var (target, time) = await ConnectAsync();
        var track = Track(seconds: 100);

        await target.LoadAsync(track, TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(99));
        var nearlyDone = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Playing, nearlyDone.State);
        Assert.Equal(TimeSpan.FromSeconds(99), nearlyDone.Position);

        time.Advance(TimeSpan.FromSeconds(1));
        var ended = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Stopped, ended.State);
        Assert.Equal(track.Duration, ended.Position);

        // Advancing the queue is the server's job: an hour later it is still parked at the end,
        // not wrapped, not counting on, not on some next track it chose for itself.
        time.Advance(TimeSpan.FromHours(1));
        var later = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Stopped, later.State);
        Assert.Equal(track.Duration, later.Position);
        Assert.Equal(track.Url, later.CurrentUri);
        Assert.Same(track, target.LoadedTrack);
    }

    [Fact]
    public async Task Poll_OvershootingTheEndBetweenPolls_StillPinsAtTheDuration()
    {
        var (target, time) = await ConnectAsync();
        var track = Track(seconds: 30);

        await target.LoadAsync(track, TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(500));

        var state = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Stopped, state.State);
        Assert.Equal(TimeSpan.FromSeconds(30), state.Position);
        Assert.Equal(TimeSpan.FromSeconds(30), state.Duration);
    }

    // ---- transport commands -------------------------------------------------------------------

    [Fact]
    public async Task Seek_MovesThePosition_AndPlaybackContinuesFromThere()
    {
        var (target, time) = await ConnectAsync();

        await target.LoadAsync(Track(seconds: 200), TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));

        await target.SeekAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(120), target.Position);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(125), target.Position);

        // Past the end clamps to the duration rather than throwing.
        await target.SeekAsync(TimeSpan.FromSeconds(9999), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(200), target.Position);
    }

    [Fact]
    public async Task Seek_OnADeviceThatCannotSeek_IsARecordedNoOp()
    {
        var options = new LoopbackOptions { Capabilities = LoopbackOptions.CapableDevice with { SupportsSeek = false } };
        var (target, _) = await ConnectAsync(options);

        // startAt is ignored on load, per the interface: no-seek devices start from zero.
        await target.LoadAsync(Track(), TimeSpan.FromSeconds(45), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, target.Position);

        await target.SeekAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, target.Position);

        var seek = Assert.Single(target.Commands, c => c.Kind == LoopbackCommandKind.Seek);
        Assert.True(seek.Ignored);
        Assert.Equal(TimeSpan.FromSeconds(60), seek.Position);
    }

    [Fact]
    public async Task Load_WithStartAt_ResumesFromThere()
    {
        var (target, time) = await ConnectAsync();

        await target.LoadAsync(Track(seconds: 300), TimeSpan.FromSeconds(90), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(90), target.Position);

        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(100), target.Position);
    }

    [Fact]
    public async Task Stop_RewindsToZero_AndTheClockStaysStopped()
    {
        var (target, time) = await ConnectAsync();

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));
        await target.StopAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));

        var state = await target.PollAsync(CancellationToken.None);
        Assert.Equal(PlaybackState.Stopped, state.State);
        Assert.Equal(TimeSpan.Zero, state.Position);
    }

    [Fact]
    public async Task Play_WithNothingLoaded_Throws()
    {
        var (target, _) = await ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.PlayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetVolume_ClampsInsteadOfThrowing()
    {
        var (target, _) = await ConnectAsync();

        await target.SetVolumeAsync(150, CancellationToken.None);
        Assert.Equal(100, target.VolumePercent);

        await target.SetVolumeAsync(-20, CancellationToken.None);
        Assert.Equal(0, target.VolumePercent);

        await target.SetVolumeAsync(37, CancellationToken.None);
        Assert.Equal(37, target.VolumePercent);

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        var state = await target.PollAsync(CancellationToken.None);
        Assert.Equal(37, state.VolumePercent);

        // The log keeps what was asked for, not what was applied.
        Assert.Equal(
            new[] { 150, -20, 37 },
            target.Commands.Where(c => c.Kind == LoopbackCommandKind.SetVolume).Select(c => c.VolumePercent!.Value));
    }

    [Fact]
    public async Task SetVolume_OnADeviceWithoutVolume_IsIgnoredAndReportsNoVolume()
    {
        var options = new LoopbackOptions { Capabilities = LoopbackOptions.CapableDevice with { SupportsVolume = false } };
        var (target, _) = await ConnectAsync(options);

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        await target.SetVolumeAsync(80, CancellationToken.None);

        var state = await target.PollAsync(CancellationToken.None);
        Assert.Null(state.VolumePercent);
        Assert.True(Assert.Single(target.Commands, c => c.Kind == LoopbackCommandKind.SetVolume).Ignored);
    }

    [Fact]
    public async Task PreloadNext_OnADeviceWithoutPreload_IsARecordedNoOp()
    {
        var options = new LoopbackOptions { Capabilities = LoopbackOptions.CapableDevice with { SupportsPreload = false } };
        var (target, _) = await ConnectAsync(options);

        await target.PreloadNextAsync(Track(), CancellationToken.None);

        Assert.Null(target.PreloadedTrack);
        Assert.True(Assert.Single(target.Commands, c => c.Kind == LoopbackCommandKind.PreloadNext).Ignored);
    }

    // ---- the command log ------------------------------------------------------------------------

    [Fact]
    public async Task CommandLog_RecordsWhatTheEngineAskedFor_InOrder()
    {
        var (target, time) = await ConnectAsync();
        var first = Track(seconds: 60, url: "http://cosine.local/playback/stream/one");
        var second = Track(seconds: 60, url: "http://cosine.local/playback/stream/two");

        await target.LoadAsync(first, TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);
        await target.PreloadNextAsync(second, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await target.SeekAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await target.SetVolumeAsync(70, CancellationToken.None);
        await target.PauseAsync(CancellationToken.None);
        await target.PreloadNextAsync(null, CancellationToken.None);
        await target.StopAsync(CancellationToken.None);
        await target.DisposeAsync();

        Assert.Equal(
            new[]
            {
                LoopbackCommandKind.Load,
                LoopbackCommandKind.Play,
                LoopbackCommandKind.PreloadNext,
                LoopbackCommandKind.Seek,
                LoopbackCommandKind.SetVolume,
                LoopbackCommandKind.Pause,
                LoopbackCommandKind.PreloadNext,
                LoopbackCommandKind.Stop,
                LoopbackCommandKind.Dispose,
            },
            target.Commands.Select(c => c.Kind));

        var preloads = target.Commands.Where(c => c.Kind == LoopbackCommandKind.PreloadNext).ToList();
        Assert.Same(second, preloads[0].Track);
        Assert.Null(preloads[1].Track);   // the hint was cleared
        Assert.All(target.Commands, c => Assert.False(c.Ignored));

        var load = target.Commands[0];
        Assert.Same(first, load.Track);
        Assert.Equal(TimeSpan.Zero, load.Position);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), load.At);
        Assert.Equal(TimeSpan.FromSeconds(10), target.Commands[3].At - load.At);
    }

    [Fact]
    public async Task CommandLog_DoesNotRecordPolls_ButPollsAreCounted()
    {
        var (target, _) = await ConnectAsync();

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        await target.PollAsync(CancellationToken.None);
        await target.PollAsync(CancellationToken.None);
        await target.PollAsync(CancellationToken.None);

        Assert.Equal(LoopbackCommandKind.Load, Assert.Single(target.Commands).Kind);
        Assert.Equal(3, target.PollCount);
    }

    [Fact]
    public async Task Pause_WhileNotPlaying_IsRecordedAsIgnored()
    {
        var (target, _) = await ConnectAsync();

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        await target.PauseAsync(CancellationToken.None);

        Assert.Equal(PlaybackState.Stopped, target.State);
        Assert.True(Assert.Single(target.Commands, c => c.Kind == LoopbackCommandKind.Pause).Ignored);
    }

    // ---- discovery ---------------------------------------------------------------------------

    [Fact]
    public async Task Discover_YieldsTheConfiguredDevices_ThenStaysOpenUntilCancelled()
    {
        var (driver, _) = Driver(LoopbackOptions.WithDevices(3));
        using var cts = new CancellationTokenSource();

        var seen = new List<DiscoveredDevice>();
        await using var devices = driver.DiscoverAsync(cts.Token).GetAsyncEnumerator();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(await devices.MoveNextAsync());
            seen.Add(devices.Current);
        }

        Assert.Equal(new[] { "Loopback Speaker 1", "Loopback Speaker 2", "Loopback Speaker 3" },
            seen.Select(d => d.DisplayName));
        Assert.Equal(new[] { "loopback-0", "loopback-1", "loopback-2" }, seen.Select(d => d.DeviceKey));
        Assert.All(seen, d => Assert.Equal("loopback", d.DriverId));

        // Real discovery does not finish when it runs out of devices; it waits for the next one.
        var pending = devices.MoveNextAsync();
        Assert.False(pending.IsCompleted);

        // Cancelling ends the sequence cleanly rather than throwing through the await foreach.
        cts.Cancel();
        Assert.False(await pending);
    }

    [Fact]
    public async Task Discover_WithAnAlreadyCancelledToken_EndsWithoutThrowing()
    {
        var (driver, _) = Driver(LoopbackOptions.WithDevices(2));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var seen = 0;
        await foreach (var _ in driver.DiscoverAsync(cts.Token))
        {
            seen++;
        }

        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task Discover_InsideAwaitForeach_TerminatesWhenCancelledFromTheLoopBody()
    {
        var (driver, _) = Driver(LoopbackOptions.WithDevices(2));
        using var cts = new CancellationTokenSource();

        var seen = new List<DiscoveredDevice>();
        await foreach (var device in driver.DiscoverAsync(cts.Token))
        {
            seen.Add(device);
            if (seen.Count == 2)
            {
                cts.Cancel();
            }
        }

        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Connect_RejectsDevicesThisDriverNeverAdvertised()
    {
        var (driver, _) = Driver();

        await Assert.ThrowsAsync<ArgumentException>(() => driver.ConnectAsync(
            new DiscoveredDevice("loopback", "loopback-99", "Ghost"), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => driver.ConnectAsync(
            new DiscoveredDevice("upnp", "uuid:abc", "Volumio"), CancellationToken.None));
    }

    [Fact]
    public async Task Connect_ExposesTheTargetToTheTest()
    {
        var (driver, _) = Driver(LoopbackOptions.WithDevices(2));

        var first = await driver.ConnectAsync(driver.Devices[0], CancellationToken.None);
        var second = await driver.ConnectAsync(driver.Devices[1], CancellationToken.None);

        Assert.Equal(2, driver.ConnectedTargets.Count);
        Assert.Same(first, driver.ConnectedTargets[0]);
        Assert.Same(second, driver.ConnectedTargets[1]);
        Assert.Same(second, driver.LastTarget);
        Assert.Equal(LoopbackOptions.CapableDevice, ((LoopbackTarget)first).Capabilities);
    }

    // ---- failure simulation --------------------------------------------------------------------

    [Fact]
    public async Task CurrentUri_IsTheLoadedTrack_UntilSomeoneElseTakesTheDevice()
    {
        var (target, _) = await ConnectAsync();
        var track = Track();

        await target.LoadAsync(track, TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(track.Url, (await target.PollAsync(CancellationToken.None)).CurrentUri);

        target.SimulateHijack("spotify:track:hijacked");

        var hijacked = await target.PollAsync(CancellationToken.None);
        Assert.Equal("spotify:track:hijacked", hijacked.CurrentUri);
        Assert.Equal("spotify:track:hijacked", target.CurrentUri);
    }

    [Fact]
    public async Task SimulateDeviceLost_MakesPollThrow()
    {
        var (target, _) = await ConnectAsync();

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        await target.PlayAsync(CancellationToken.None);
        target.SimulateDeviceLost();

        await Assert.ThrowsAsync<IOException>(() => target.PollAsync(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => target.PauseAsync(CancellationToken.None));

        // Teardown of a dead session must still work.
        await target.DisposeAsync();
        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task SimulateDeviceLost_UsesTheSuppliedException_AndCanBeUndone()
    {
        var (target, _) = await ConnectAsync();
        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);

        target.SimulateDeviceLost(new TimeoutException("gone"));
        await Assert.ThrowsAsync<TimeoutException>(() => target.PollAsync(CancellationToken.None));

        target.SimulateDeviceRestored();
        Assert.Equal(PlaybackState.Stopped, (await target.PollAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndFurtherCommandsThrow()
    {
        var (target, _) = await ConnectAsync();

        await target.LoadAsync(Track(), TimeSpan.Zero, CancellationToken.None);
        await target.DisposeAsync();
        await target.DisposeAsync();

        Assert.Equal(1, target.Commands.Count(c => c.Kind == LoopbackCommandKind.Dispose));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => target.PlayAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => target.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Commands_HonourCancellation()
    {
        var (target, _) = await ConnectAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.PollAsync(cts.Token));
    }
}
