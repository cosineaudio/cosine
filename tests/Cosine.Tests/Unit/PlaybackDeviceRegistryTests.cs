using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Hubs;
using Cosine.Infrastructure.Playback;
using Cosine.Core.Options;
using Cosine.Playback.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

/// <summary>
/// The device half of <see cref="PlaybackSessionManager"/>: reconciling what discovery reports
/// against persisted rows, and deciding which devices are still on the network.
/// <para>
/// Presence is the interesting part. Phase 1 treated a device as online forever once seen, so
/// an unplugged speaker stayed offerable until cosine restarted and picking it failed with a
/// connection timeout instead of an honest "not seen". Drivers now report how long a sighting is
/// good for (<c>DiscoveredDevice.ValidFor</c> - SSDP's <c>CACHE-CONTROL: max-age</c>, mDNS's
/// record TTL), and these tests are what hold that behaviour in place.
/// </para>
/// <para>Nothing here waits on real time: the manager's clock is driven by hand.</para>
/// </summary>
public class PlaybackDeviceRegistryTests
{
    private const string Driver = "upnp";

    /// <summary>A clock a test moves by assignment.</summary>
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>
    /// Hands every scope the same in-memory context, so a row written through one
    /// <c>CreateAsyncScope</c> is visible to the next. Only the two services the device paths
    /// need are resolvable - anything else the manager reached for would fail loudly rather than
    /// silently receiving null.
    /// </summary>
    private sealed class SharedDbScopeFactory(CosineDbContext db)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) => serviceType switch
        {
            _ when serviceType == typeof(CosineDbContext) => db,
            _ when serviceType == typeof(IPlaybackStreamResolver) => new ConfiguredResolver(),
            _ when serviceType == typeof(ITranscodingService) => new StubTranscoder(),
            _ => null,
        };

        public void Dispose() { }
    }

    /// <summary>
    /// Knows which formats exist and nothing else. Validating a device's transcode profile is the
    /// only thing the device paths ask a transcoder for; a call to encode anything would be this
    /// test reaching past what it is about.
    /// </summary>
    private sealed class StubTranscoder : ITranscodingService
    {
        private static readonly string[] Formats = ["mp3", "flac", "aac", "opus", "ogg", "wav"];

        public bool SupportsFormat(string format) =>
            Formats.Contains(format, StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<TranscodeFormat>> GetSupportedFormatsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TranscodeFormat>>(
                Formats.Select(f => new TranscodeFormat(f, f, [128, 192, 320])).ToList());

        public Task<Stream> GetTranscodeStreamAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long> MaterializeAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> PruneCacheAsync(long maxDiskBytes, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string GetContentType(string format) => throw new NotSupportedException();

        public TranscodeActivity GetActivity() => throw new NotSupportedException();
    }

    /// <summary>
    /// Configured, so <c>StartSessionAsync</c> gets past its base-URL check and reaches the
    /// presence check these tests are about. Nothing else on it is reached: a session that got
    /// as far as resolving a track would be testing something else.
    /// </summary>
    private sealed class ConfiguredResolver : IPlaybackStreamResolver
    {
        public bool IsConfigured => true;

        public Task<TrackPresentation> ResolveAsync(
            Guid sessionId, Guid trackId, Guid userId,
            PlaybackCapabilities capabilities, string? transcodeProfile, CancellationToken ct)
            => throw new NotSupportedException();

        public bool TryValidateToken(string token, out PlaybackStreamTicket ticket)
            => throw new NotSupportedException();

        public bool TryValidateCoverArtToken(string token, out PlaybackCoverArtTicket ticket)
            => throw new NotSupportedException();
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

    private static (PlaybackSessionManager Manager, TestTime Time, CosineDbContext Db) Build()
    {
        var db = TestDbContextFactory.Create();
        var time = new TestTime();
        var manager = new PlaybackSessionManager(
            new SharedDbScopeFactory(db),
            [],
            new SilentHubContext(),
            Options.Create(new PlaybackOptions { AdvertisedBaseUrl = "http://192.168.1.10:8080" }),
            NullLogger<PlaybackSessionManager>.Instance,
            time);

        return (manager, time, db);
    }

    private static DiscoveredDevice Device(TimeSpan? validFor = null, string key = "uuid:volumio-1") =>
        new(Driver, key, "Volumio", Address: "192.168.1.42", Model: "upmpdcli", ValidFor: validFor);

    [Fact]
    public async Task FirstSighting_InsertsARowAndListsTheDeviceOnline()
    {
        var (manager, _, _) = Build();

        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);

        var device = Assert.Single(await manager.GetDevicesAsync(default));
        Assert.True(device.Online);
        Assert.Equal("Volumio", device.DisplayName);
        Assert.Equal("192.168.1.42", device.Address);
    }

    [Fact]
    public async Task RediscoveringTheSameDevice_DoesNotDuplicateTheRow()
    {
        // Renderers re-announce every few seconds; reconciliation is by (DriverId, DeviceKey).
        var (manager, _, _) = Build();

        for (var i = 0; i < 5; i++)
            await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);

        Assert.Single(await manager.GetDevicesAsync(default));
    }

    [Fact]
    public async Task ASightingWithNoExpiry_StaysOnlineIndefinitely()
    {
        // Not knowing how long a device will be around is a different statement from knowing it
        // has gone, so a driver that reports nothing gets the Phase 1 behaviour.
        var (manager, time, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(validFor: null), default);

        time.Advance(TimeSpan.FromDays(7));

        Assert.True(Assert.Single(await manager.GetDevicesAsync(default)).Online);
    }

    [Fact]
    public async Task ASightingGoesOffline_OnceItsAdvertisedLifetimeHasPassed()
    {
        var (manager, time, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.True(Assert.Single(await manager.GetDevicesAsync(default)).Online);

        time.Advance(TimeSpan.FromMinutes(2));
        var expired = Assert.Single(await manager.GetDevicesAsync(default));

        // The row survives - it carries the user's per-device settings, and the device will very
        // likely be back. Only presence changed.
        Assert.False(expired.Online);
        Assert.Equal("Volumio", expired.DisplayName);
    }

    [Fact]
    public async Task ReAnnouncing_ExtendsTheLifetime()
    {
        var (manager, time, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);

        time.Advance(TimeSpan.FromMinutes(25));
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);
        time.Advance(TimeSpan.FromMinutes(20));

        Assert.True(Assert.Single(await manager.GetDevicesAsync(default)).Online);
    }

    [Fact]
    public async Task AGoodbye_TakesTheDeviceOfflineImmediately()
    {
        // ssdp:byebye, and an mDNS goodbye, are both a lifetime of zero. Waiting out a max-age
        // that can be half an hour long when the device has said it is leaving is pure lag.
        var (manager, _, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);

        await manager.RegisterDiscoveredAsync(Device(TimeSpan.Zero), default);

        Assert.False(Assert.Single(await manager.GetDevicesAsync(default)).Online);
    }

    [Fact]
    public async Task AGoodbyeForAnUnknownDevice_DoesNotCreateARow()
    {
        // A byebye from something cosine never saw is not a discovery. Recording it would put a
        // device in the picker that has just announced it is gone.
        var (manager, _, _) = Build();

        await manager.RegisterDiscoveredAsync(Device(TimeSpan.Zero, key: "uuid:never-seen"), default);

        Assert.Empty(await manager.GetDevicesAsync(default));
    }

    [Fact]
    public async Task ADeviceThatComesBackAfterAGoodbye_IsOnlineAgain()
    {
        var (manager, _, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.Zero), default);

        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);

        var devices = await manager.GetDevicesAsync(default);
        Assert.True(Assert.Single(devices).Online);
    }

    [Fact]
    public async Task StartingASessionOnAnExpiredDevice_ReportsItOffline()
    {
        // The failure a user sees must name the real problem. Attempting to connect to an
        // address nothing answers on produces a timeout that reads as a cosine bug.
        var (manager, time, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);
        var deviceId = Assert.Single(await manager.GetDevicesAsync(default)).Id;

        time.Advance(TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<PlaybackDeviceOfflineException>(() =>
            manager.StartSessionAsync(
                deviceId, new PlaybackCaller(Guid.NewGuid(), "kenny", IsAdmin: true), takeOver: false, default));
    }

    [Fact]
    public async Task AnAdvertisedNameChange_IsPickedUp()
    {
        var (manager, time, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);

        // Past the last-seen write interval, so the row is rewritten rather than skipped.
        time.Advance(TimeSpan.FromMinutes(5));
        await manager.RegisterDiscoveredAsync(
            Device(TimeSpan.FromMinutes(30)) with { DisplayName = "Kitchen" }, default);

        Assert.Equal("Kitchen", Assert.Single(await manager.GetDevicesAsync(default)).DisplayName);
    }

    // ---- Admin settings ----
    //
    // The four things an admin pins to a device. They existed as columns from the start and had
    // no way in but SQL, which is how a README came to recommend one of them as the fix for a
    // speaker that refuses hi-res files.

    private static async Task<(PlaybackSessionManager Manager, Guid DeviceId)> BuildWithDeviceAsync()
    {
        var (manager, _, _) = Build();
        await manager.RegisterDiscoveredAsync(Device(TimeSpan.FromMinutes(30)), default);
        return (manager, Assert.Single(await manager.GetDevicesAsync(default)).Id);
    }

    [Fact]
    public async Task AChosenName_WinsOverTheAdvertisedOne_WhichIsStillReported()
    {
        var (manager, deviceId) = await BuildWithDeviceAsync();

        var updated = await manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(DisplayName: "Bedroom"), default);

        Assert.Equal("Bedroom", updated.DisplayName);
        // Both are reported: the page has to be able to say what is being overridden, and clearing
        // the override needs something to fall back to.
        Assert.Equal("Volumio", updated.AdvertisedName);
    }

    [Fact]
    public async Task AnEmptyName_ClearsTheOverride_RatherThanBlankingTheDevice()
    {
        var (manager, deviceId) = await BuildWithDeviceAsync();
        await manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(DisplayName: "Bedroom"), default);

        var cleared = await manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(DisplayName: "  "), default);

        Assert.Equal("Volumio", cleared.DisplayName);
    }

    [Fact]
    public async Task SettingsNotSent_AreLeftAlone()
    {
        // PATCH semantics on a PUT body: the page edits one thing without restating the rest,
        // and an absent field must not read as "set it to null".
        var (manager, deviceId) = await BuildWithDeviceAsync();
        await manager.UpdateDeviceAsync(
            deviceId,
            new PlaybackDeviceSettings(DisplayName: "Bedroom", TranscodeProfile: "mp3:320"),
            default);

        var updated = await manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(Enabled: false), default);

        Assert.Equal("Bedroom", updated.DisplayName);
        Assert.Equal("mp3:320", updated.TranscodeProfile);
        Assert.False(updated.Enabled);
    }

    [Fact]
    public async Task AProfileNamingAFormatCosineCannotProduce_IsRefused()
    {
        // Refused now, with somebody there to read why. Stored, it would surface days later as a
        // speaker that goes quiet, with the cause three days back in a log.
        var (manager, deviceId) = await BuildWithDeviceAsync();

        var ex = await Assert.ThrowsAsync<PlaybackSettingInvalidException>(() =>
            manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(TranscodeProfile: "wma:320"), default));

        Assert.Contains("wma", ex.Message);
        // The alternatives are named, because "no" without them is a guessing game.
        Assert.Contains("mp3", ex.Message);
        Assert.Null(Assert.Single(await manager.GetDevicesAsync(default)).TranscodeProfile);
    }

    [Theory]
    [InlineData("mp3:0")]
    [InlineData("mp3:notanumber")]
    [InlineData("mp3:99999")]
    public async Task AProfileWithANonsenseBitRate_IsRefused(string profile)
    {
        var (manager, deviceId) = await BuildWithDeviceAsync();

        await Assert.ThrowsAsync<PlaybackSettingInvalidException>(() =>
            manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(TranscodeProfile: profile), default));
    }

    [Fact]
    public async Task AnEmptyProfile_MeansTheOriginalFile()
    {
        var (manager, deviceId) = await BuildWithDeviceAsync();
        await manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(TranscodeProfile: "flac"), default);

        var cleared = await manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(TranscodeProfile: ""), default);

        Assert.Null(cleared.TranscodeProfile);
    }

    [Fact]
    public async Task AVolumeCeilingOutsideAPercentage_IsRefused()
    {
        var (manager, deviceId) = await BuildWithDeviceAsync();

        await Assert.ThrowsAsync<PlaybackSettingInvalidException>(() =>
            manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(MaxVolumePercent: 140), default));
    }

    [Fact]
    public async Task ALyricOffset_IsStoredAndListed_InBothDirections()
    {
        // Negative is as legitimate as positive: it is a shift, not a delay, and a device that
        // reports its position late needs the lyrics pulled forward rather than held back.
        var (manager, deviceId) = await BuildWithDeviceAsync();

        var delayed = await manager.UpdateDeviceAsync(
            deviceId, new PlaybackDeviceSettings(LyricOffsetMs: 2000), default);
        Assert.Equal(2000, delayed.LyricOffsetMs);

        var advanced = await manager.UpdateDeviceAsync(
            deviceId, new PlaybackDeviceSettings(LyricOffsetMs: -500), default);
        Assert.Equal(-500, advanced.LyricOffsetMs);

        // On the picker's list too, not only on the admin response: that list is where the web
        // player reads the offset from.
        Assert.Equal(-500, Assert.Single(await manager.GetDevicesAsync(default)).LyricOffsetMs);
    }

    [Theory]
    [InlineData(30_000)]
    [InlineData(-30_000)]
    public async Task ALyricOffsetBeyondAnySpeakerBuffer_IsRefused(int offsetMs)
    {
        var (manager, deviceId) = await BuildWithDeviceAsync();

        await Assert.ThrowsAsync<PlaybackSettingInvalidException>(() =>
            manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(LyricOffsetMs: offsetMs), default));
    }

    [Fact]
    public async Task ADisabledDevice_IsStillListed_ButRefusesASession()
    {
        // Listed because the admin has to be able to find it again to switch it back on; refused
        // because the picker greying it out is a courtesy and not a rule.
        var (manager, deviceId) = await BuildWithDeviceAsync();
        await manager.UpdateDeviceAsync(deviceId, new PlaybackDeviceSettings(Enabled: false), default);

        Assert.False(Assert.Single(await manager.GetDevicesAsync(default)).Enabled);

        await Assert.ThrowsAsync<PlaybackDeviceDisabledException>(() =>
            manager.StartSessionAsync(
                deviceId, new PlaybackCaller(Guid.NewGuid(), "kenny", IsAdmin: true), takeOver: false, default));
    }

    [Fact]
    public async Task EditingADeviceThatIsNotThere_IsANotFound()
    {
        var (manager, _, _) = Build();

        await Assert.ThrowsAsync<PlaybackDeviceNotFoundException>(() =>
            manager.UpdateDeviceAsync(Guid.NewGuid(), new PlaybackDeviceSettings(Enabled: false), default));
    }
}
