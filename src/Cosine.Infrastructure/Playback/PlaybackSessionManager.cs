using System.Collections.Concurrent;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Hubs;
using Cosine.Playback.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Playback;

/// <summary>
/// Owns every live playback session. Singleton, following the <c>NowPlayingService</c> precedent:
/// the connected device and the poll state are process-local live objects that no request scope
/// can hold. Database rows exist so a restart can resume and so the Subsonic jukebox API has
/// something to read (plan §4.4).
/// </summary>
public sealed class PlaybackSessionManager : IPlaybackSessionManager, IAsyncDisposable
{
    /// <summary>
    /// Position is written back no more often than this. State and queue changes persist
    /// immediately; position alone would otherwise be a database write every second per device
    /// to save something that is only read after a restart.
    /// </summary>
    private static readonly TimeSpan PositionPersistInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often an unchanged device's last-seen stamp is written back.</summary>
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The last.fm rule, the same one <c>player.ts</c> applies: a play counts once half the track
    /// or four minutes of it has been heard. It lives here as well as in the browser because a
    /// remote session's audio never touches the browser - there is no <c>timeupdate</c> to hang it
    /// off - and the two can't both fire for one play, since exactly one of them is playing the
    /// track (plan §5 Phase 3).
    /// </summary>
    private const double ScrobbleFraction = 0.5;
    private static readonly TimeSpan ScrobbleAfter = TimeSpan.FromMinutes(4);

    /// <summary>
    /// How few tracks may be left ahead of the playing one before auto-play fetches more. Topping
    /// up early rather than at the end is what keeps the gapless hint armed: waiting for the queue
    /// to actually run out means the device has already been told there is nothing next.
    /// </summary>
    private const int AutoPlayRefillThreshold = 3;

    /// <summary>How many tracks one top-up adds. Small enough to keep following what is being
    /// played rather than committing an hour ahead to whatever was on at the time.</summary>
    private const int AutoPlayBatchSize = 20;

    /// <summary>
    /// How many played entries stay in the queue behind the current one. An auto-playing session
    /// runs for hours, and every entry is a persisted row and a line in the queue panel.
    /// </summary>
    private const int AutoPlayHistoryLimit = 50;

    /// <summary>
    /// How far back "don't play this again" reaches. Longer than the queue keeps, so a track
    /// trimmed out of the queue does not immediately become a candidate again.
    /// </summary>
    private const int PlayedLogLimit = 500;

    /// <summary>How many recently played tracks seed a top-up. Their distinct artists are what
    /// the blend is actually built from, and the service caps those at three.</summary>
    private const int AutoPlaySeedCount = 8;

    private readonly IServiceScopeFactory _scopes;
    private readonly IReadOnlyList<IPlaybackDriver> _drivers;
    private readonly IHubContext<PlaybackHub> _hub;
    private readonly PlaybackOptions _options;
    private readonly ILogger<PlaybackSessionManager> _log;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();

    /// <summary>
    /// Devices discovery currently believes are on the network, keyed by
    /// <c>(DriverId, DeviceKey)</c>. A device cosine has a row for but no live sighting of is
    /// listed as offline: it has no current address, so there is nothing to connect to.
    /// <para>
    /// Sightings expire. A driver that reports how long its sighting is good for -
    /// <c>DiscoveredDevice.ValidFor</c>, which is SSDP's <c>CACHE-CONTROL: max-age</c> and mDNS's
    /// record TTL - has that honoured, so a speaker that is unplugged stops being offered
    /// instead of staying listed as online until cosine restarts. Expiry is applied lazily on
    /// read rather than by a timer: nothing observes the set except the picker and the connect
    /// path, so a sweep would be work done for nobody.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<(string DriverId, string DeviceKey), DeviceSighting> _discovered = new();

    /// <param name="ExpiresAt">
    /// Null when the driver gave no expiry information, which means the sighting never goes
    /// stale on its own. That is the honest reading: not knowing how long a device will be
    /// around is different from knowing it has gone.
    /// </param>
    private readonly record struct DeviceSighting(DiscoveredDevice Device, DateTimeOffset? ExpiresAt);

    /// <summary>When each device's row was last written, to keep re-announcements off the database.</summary>
    private readonly ConcurrentDictionary<(string DriverId, string DeviceKey), DateTimeOffset> _lastSeenWrites = new();

    public PlaybackSessionManager(
        IServiceScopeFactory scopes,
        IEnumerable<IPlaybackDriver> drivers,
        IHubContext<PlaybackHub> hub,
        IOptions<PlaybackOptions> options,
        ILogger<PlaybackSessionManager> log,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _drivers = drivers.ToList();
        _hub = hub;
        _options = options.Value;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyCollection<Guid> ActiveSessionIds => _sessions.Keys.ToList();

    public bool IsSessionLive(Guid sessionId) => _sessions.ContainsKey(sessionId);

    public PlaybackStateSnapshot? TryGetState(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s.Snapshot(_time.GetUtcNow()) : null;

    public PlaybackQueueSnapshot? TryGetQueue(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s.QueueSnapshot() : null;

    // ---- Devices ----

    public async Task RegisterDiscoveredAsync(DiscoveredDevice device, CancellationToken ct)
    {
        var key = (device.DriverId, device.DeviceKey);
        var now = _time.GetUtcNow();

        // A sighting good for no time at all is the device announcing its own departure -
        // SSDP ssdp:byebye, an mDNS goodbye. Drop it and write nothing: stamping LastSeenAt for
        // a device that just said it is leaving would be recording the opposite of what happened.
        if (device.ValidFor == TimeSpan.Zero)
        {
            if (_discovered.TryRemove(key, out _))
            {
                _lastSeenWrites.TryRemove(key, out _);
                _log.LogInformation(
                    "Playback device {DisplayName} ({DriverId}/{DeviceKey}) announced it is leaving",
                    device.DisplayName, device.DriverId, device.DeviceKey);
            }

            return;
        }

        var known = _discovered.TryGetValue(key, out var previous);
        _discovered[key] = new DeviceSighting(
            device, device.ValidFor is { } validFor ? now + validFor : null);

        // SSDP renderers re-announce every few seconds. The in-memory entry above is what the
        // picker reads, so the row only needs writing when something in it actually changed or
        // when its last-seen stamp has gone stale - otherwise this would be a database write per
        // device per announcement, to store what nobody reads until a restart.
        if (known && previous.Device == device
            && _lastSeenWrites.TryGetValue(key, out var writtenAt)
            && now - writtenAt < LastSeenWriteInterval)
        {
            return;
        }

        _lastSeenWrites[key] = _time.GetUtcNow();

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var row = await db.PlaybackDevices
            .FirstOrDefaultAsync(d => d.DriverId == device.DriverId && d.DeviceKey == device.DeviceKey, ct);

        if (row is null)
        {
            row = new PlaybackDevice
            {
                Id = Guid.NewGuid(),
                DriverId = device.DriverId,
                DeviceKey = device.DeviceKey,
                DisplayName = device.DisplayName,
            };
            db.PlaybackDevices.Add(row);
            _log.LogInformation(
                "Discovered playback device {DisplayName} ({DriverId}/{DeviceKey})",
                device.DisplayName, device.DriverId, device.DeviceKey);
        }

        // The advertised name can change; a user's chosen name is kept separately and wins.
        row.DisplayName = device.DisplayName;
        row.LastSeenAt = _time.GetUtcNow();
        row.LastAddress = device.Address;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The current sighting for a device, or null if there is none or it has expired. Expired
    /// entries are removed as they are found, which is the whole of the expiry mechanism -
    /// every read goes through here.
    /// </summary>
    private DiscoveredDevice? TryGetLiveSighting((string DriverId, string DeviceKey) key)
    {
        if (!_discovered.TryGetValue(key, out var sighting))
        {
            return null;
        }

        if (sighting.ExpiresAt is { } expiresAt && expiresAt <= _time.GetUtcNow())
        {
            // Remove the exact entry read, so a re-announcement that landed between the read and
            // here is not thrown away along with the stale one.
            _discovered.TryRemove(new KeyValuePair<(string, string), DeviceSighting>(key, sighting));
            _log.LogInformation(
                "Playback device {DriverId}/{DeviceKey} has not been seen since its announcement expired",
                key.DriverId, key.DeviceKey);
            return null;
        }

        return sighting.Device;
    }

    public async Task<IReadOnlyList<PlaybackDeviceInfo>> GetDevicesAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var rows = await db.PlaybackDevices.AsNoTracking().OrderBy(d => d.DisplayName).ToListAsync(ct);

        return rows.Select(Describe).ToList();
    }

    /// <summary>One persisted row merged with what discovery and the live sessions know.</summary>
    private PlaybackDeviceInfo Describe(PlaybackDevice row)
    {
        var online = TryGetLiveSighting((row.DriverId, row.DeviceKey)) is not null;
        var session = _sessions.Values.FirstOrDefault(s => s.DeviceId == row.Id);

        return new PlaybackDeviceInfo(
            row.Id, row.DriverId, row.DeviceKey,
            row.UserDisplayName ?? row.DisplayName,
            row.DisplayName,
            online, row.LastAddress, row.TranscodeProfile, row.MaxVolumePercent, row.LyricOffsetMs,
            row.Enabled, row.LastSeenAt, session?.Id, session?.OwnerUsername);
    }

    public async Task<PlaybackDeviceInfo> UpdateDeviceAsync(
        Guid deviceId, PlaybackDeviceSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var row = await db.PlaybackDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                  ?? throw new PlaybackDeviceNotFoundException(deviceId);

        if (settings.DisplayName is { } name)
        {
            // Empty is not the same as absent: it is "stop overriding", and the advertised name
            // takes back over. Absent is null, and never reaches here.
            var trimmed = name.Trim();
            row.UserDisplayName = trimmed.Length == 0 ? null : trimmed;
        }

        if (settings.TranscodeProfile is { } profile)
        {
            var trimmed = profile.Trim();
            row.TranscodeProfile = trimmed.Length == 0
                ? null
                : await ValidateProfileAsync(trimmed, scope.ServiceProvider, ct);
        }

        if (settings.MaxVolumePercent is { } max)
        {
            if (max is < 0 or > 100)
                throw new PlaybackSettingInvalidException(
                    $"A volume ceiling of {max}% is not a percentage. Use 0–100.");

            row.MaxVolumePercent = max;
        }

        if (settings.LyricOffsetMs is { } offset)
        {
            const int bound = PlaybackDeviceSettings.MaxLyricOffsetMs;
            if (Math.Abs(offset) > bound)
                throw new PlaybackSettingInvalidException(
                    $"A lyric offset of {offset} ms is not a speaker buffer. Use −{bound} to {bound}.");

            row.LyricOffsetMs = offset;
        }

        if (settings.Enabled is { } enabled) row.Enabled = enabled;

        await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Playback device {Device} updated: name={Name}, profile={Profile}, maxVolume={MaxVolume}, enabled={Enabled}",
            row.DisplayName, row.UserDisplayName, row.TranscodeProfile, row.MaxVolumePercent, row.Enabled);

        if (_sessions.Values.FirstOrDefault(s => s.DeviceId == row.Id) is { } live)
        {
            await ApplyToLiveSessionAsync(live, row, ct);
        }

        return Describe(row);
    }

    /// <summary>
    /// Carry the new settings into a session that is already running. Only the volume ceiling has
    /// to reach the device - a name is cosmetic and a transcode profile is read afresh for the
    /// next track - and the ceiling has to, because a ceiling that waits for the next session
    /// leaves the speaker exactly as loud as the admin just said it must not be.
    /// <para>
    /// Disabling is deliberately not enforced here. It stops the device being offered and stops
    /// new sessions; cutting off music someone is listening to right now is a different act, and
    /// the admin has takeover and end-session for that.
    /// </para>
    /// </summary>
    private async Task ApplyToLiveSessionAsync(LiveSession session, PlaybackDevice row, CancellationToken ct)
    {
        session.DeviceName = row.UserDisplayName ?? row.DisplayName;
        session.TranscodeProfile = row.TranscodeProfile;
        session.MaxVolumePercent = row.MaxVolumePercent;

        if (session.VolumePercent <= row.MaxVolumePercent) return;

        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Target.Capabilities.SupportsVolume)
            {
                await session.Target.SetVolumeAsync(row.MaxVolumePercent, ct);
            }

            session.VolumePercent = row.MaxVolumePercent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The setting is saved either way; a device that would not take the volume now is not
            // a reason to fail the admin's edit.
            _log.LogWarning(ex,
                "Could not lower {Device} to its new {MaxVolume}% ceiling", session.DeviceName, row.MaxVolumePercent);
        }
        finally
        {
            session.Gate.Release();
        }

        await PublishStateAsync(session, session.Snapshot(_time.GetUtcNow()), ct);
    }

    /// <summary>
    /// A profile is <c>format</c> or <c>format:bitrate</c>, and the format has to be one ffmpeg
    /// is wired for here - the same check the resolver would make later, made now while there is
    /// somebody to tell.
    /// </summary>
    private static async Task<string> ValidateProfileAsync(
        string profile, IServiceProvider sp, CancellationToken ct)
    {
        var parts = profile.Split(':', 2);
        var format = parts[0].Trim().ToLowerInvariant();

        var transcoder = sp.GetRequiredService<ITranscodingService>();
        if (format.Length == 0 || !transcoder.SupportsFormat(format))
        {
            // Only asked for on the way to failing, because it is the one call here that can go
            // out to ffmpeg - and the whole value of it is naming the alternatives in the error.
            var available = await transcoder.GetSupportedFormatsAsync(ct);
            throw new PlaybackSettingInvalidException(
                $"cosine cannot produce \"{format}\". Supported: " +
                $"{string.Join(", ", available.Select(f => f.Name))}.");
        }

        if (parts.Length == 1) return format;

        var rate = parts[1].Trim();
        if (!int.TryParse(rate, out var kbps) || kbps is < 32 or > 2048)
        {
            throw new PlaybackSettingInvalidException(
                $"\"{rate}\" is not a bit rate in kbps. Use 32–2048, or leave it off for the format's default.");
        }

        return $"{format}:{kbps}";
    }

    // ---- Session lifecycle ----

    public async Task<PlaybackStateSnapshot> StartSessionAsync(
        Guid deviceId, PlaybackCaller caller, bool takeOver, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPlaybackStreamResolver>();

        if (!resolver.IsConfigured) throw new PlaybackNotConfiguredException();

        var row = await db.PlaybackDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                  ?? throw new PlaybackDeviceNotFoundException(deviceId);

        // Checked here as well as in the picker, which already greys a disabled device out: the
        // picker is a courtesy, this is the rule.
        if (!row.Enabled)
            throw new PlaybackDeviceDisabledException(deviceId, row.UserDisplayName ?? row.DisplayName);

        var existing = _sessions.Values.FirstOrDefault(s => s.DeviceId == deviceId);
        if (existing is not null)
        {
            if (existing.OwnerUserId == caller.UserId) return existing.Snapshot(_time.GetUtcNow());

            // A speaker is a shared physical object, so "someone else is using it" is a normal
            // state that needs an answer rather than a race. Only an admin may take it, and only
            // by asking explicitly - see the note in the plan discussion.
            if (!(takeOver && caller.IsAdmin))
                throw new PlaybackDeviceBusyException(deviceId, existing.OwnerUserId, existing.OwnerUsername);

            _log.LogInformation(
                "{Admin} is taking over device {Device} from {Owner}",
                caller.Username, row.DisplayName, existing.OwnerUsername);
            await TerminateAsync(existing, ct);
        }

        if (TryGetLiveSighting((row.DriverId, row.DeviceKey)) is not { } discovered)
            throw new PlaybackDeviceOfflineException(deviceId, row.UserDisplayName ?? row.DisplayName);

        var driver = _drivers.FirstOrDefault(d => d.DriverId == row.DriverId)
                     ?? throw new PlaybackDeviceOfflineException(deviceId, row.UserDisplayName ?? row.DisplayName);

        var target = await driver.ConnectAsync(discovered, ct);

        var session = new LiveSession(
            Guid.NewGuid(), row.Id, row.UserDisplayName ?? row.DisplayName, row.TranscodeProfile,
            caller.UserId, caller.Username, target)
        {
            MaxVolumePercent = row.MaxVolumePercent,
            VolumePercent = Math.Min(100, row.MaxVolumePercent),
        };

        // Any stale row for this device is from a previous process; the live session replaces it.
        var stale = await db.PlaybackSessions.Where(s => s.DeviceId == deviceId).ToListAsync(ct);
        if (stale.Count > 0) db.PlaybackSessions.RemoveRange(stale);

        db.PlaybackSessions.Add(new PlaybackSession
        {
            Id = session.Id,
            DeviceId = row.Id,
            OwnerUserId = caller.UserId,
            State = PlaybackSessionState.Idle,
            VolumePercent = session.VolumePercent,
        });
        await db.SaveChangesAsync(ct);

        _sessions[session.Id] = session;
        _log.LogInformation(
            "Playback session {Session} opened on {Device} for {User}",
            session.Id, session.DeviceName, caller.Username);

        var snapshot = session.Snapshot(_time.GetUtcNow());
        await PublishStateAsync(session, snapshot, ct);
        return snapshot;
    }

    public async Task EndSessionAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct)
    {
        var session = Require(sessionId);
        Authorize(session, caller);
        await TerminateAsync(session, ct);
    }

    private async Task TerminateAsync(LiveSession session, CancellationToken ct)
    {
        _sessions.TryRemove(session.Id, out _);

        // Before the device is touched: an arm still resolving would otherwise finish its encode
        // for a session that is gone, and then reach for a target this method has disposed.
        // ArmPreloadAsync swallows its own failures, so this await cannot fault.
        await session.Lifetime.CancelAsync();
        await session.PreloadWork;
        session.Lifetime.Dispose();

        await session.Gate.WaitAsync(ct);
        try
        {
            try
            {
                await session.Target.StopAsync(ct);
            }
            catch (Exception ex)
            {
                // The device may already be gone; that is not a reason to leak the session.
                _log.LogDebug(ex, "Stop failed while ending session {Session}", session.Id);
            }

            await session.Target.DisposeAsync();
        }
        finally
        {
            session.Gate.Release();
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        await db.PlaybackSessions.Where(s => s.Id == session.Id).ExecuteDeleteAsync(ct);

        // Ending the session is what revokes its outstanding stream tokens (plan §4.3a), so the
        // client needs to know the URLs it holds are now dead.
        await _hub.Clients.Group(PlaybackHub.SessionGroup(session.Id))
            .SendAsync("PlaybackSessionEnded", session.Id, ct);

        _log.LogInformation("Playback session {Session} ended", session.Id);
    }

    // ---- Queue ----

    public Task<PlaybackStateSnapshot> SetQueueAsync(
        Guid sessionId, PlaybackCaller caller, IReadOnlyList<Guid> trackIds, int startIndex, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, sp, c) =>
        {
            await LoadMetadataAsync(s, trackIds, sp, c);
            var move = s.Queue.SetQueue(trackIds, startIndex);
            await ApplyAsync(s, move, sp, c);
            // A handful of tracks handed to an auto-playing session is a seed, not a queue; it
            // should not have to reach the end of them before anything else is fetched.
            await MaybeAutoPlayAsync(s, sp, c);
        }, ct);

    public Task<PlaybackStateSnapshot> AddToQueueAsync(
        Guid sessionId, PlaybackCaller caller, IReadOnlyList<Guid> trackIds, int? insertAt, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, sp, c) =>
        {
            await LoadMetadataAsync(s, trackIds, sp, c);
            var move = s.Queue.Add(trackIds, insertAt);
            await ApplyAsync(s, move, sp, c);
            // Appending to a queue whose last track is playing - an endless radio does exactly
            // this - changes what comes next from nothing to something, and the device is the one
            // that acts on that.
            SchedulePreload(s);
        }, ct);

    public Task<PlaybackStateSnapshot> RemoveFromQueueAsync(
        Guid sessionId, PlaybackCaller caller, int position, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, sp, c) =>
        {
            await ApplyAsync(s, s.Queue.Remove(position), sp, c);
            // Dropping the entry after the current one leaves the device holding a track the
            // queue no longer has.
            SchedulePreload(s);
        }, ct);

    public Task<PlaybackStateSnapshot> MoveInQueueAsync(
        Guid sessionId, PlaybackCaller caller, int from, int to, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, sp, c) =>
        {
            await ApplyAsync(s, s.Queue.Move(from, to), sp, c);
            // Reordering doesn't change what is playing, but it can change what comes after it.
            SchedulePreload(s);
        }, ct);

    public Task<PlaybackStateSnapshot> PlayAtAsync(
        Guid sessionId, PlaybackCaller caller, int position, CancellationToken ct) =>
        MutateAsync(sessionId, caller, (s, sp, c) => ApplyAsync(s, s.Queue.PlayAt(position), sp, c), ct);

    // ---- Transport ----

    public Task<PlaybackStateSnapshot> PlayAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, sp, c) =>
        {
            if (s.Queue.Count == 0) return;

            // Nothing loaded yet (a fresh session, or one that ran to the end) starts the
            // current queue entry; otherwise this is a resume and the device keeps its position.
            if (s.LoadedTrackId is null)
                await StartTrackAsync(s, s.Queue.Index, TimeSpan.Zero, sp, c);
            else
            {
                await s.Target.PlayAsync(c);
                s.State = PlaybackSessionState.Playing;
            }
        }, ct);

    public Task<PlaybackStateSnapshot> PauseAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, _, c) =>
        {
            await s.Target.PauseAsync(c);
            s.State = PlaybackSessionState.Paused;
        }, ct);

    public Task<PlaybackStateSnapshot> StopAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, _, c) =>
        {
            await s.Target.StopAsync(c);
            s.State = PlaybackSessionState.Stopped;
            s.Position = TimeSpan.Zero;
            s.LoadedTrackId = null;
        }, ct);

    public Task<PlaybackStateSnapshot> NextAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, sp, c) =>
        {
            // Skipping through the last of the queue is the same end as playing through it.
            await MaybeAutoPlayAsync(s, sp, c);
            await ApplyAsync(s, s.Queue.Next(), sp, c);
        }, ct);

    public Task<PlaybackStateSnapshot> PreviousAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct) =>
        MutateAsync(sessionId, caller, (s, sp, c) => ApplyAsync(s, s.Queue.Previous(s.Position), sp, c), ct);

    public Task<PlaybackStateSnapshot> SeekAsync(
        Guid sessionId, PlaybackCaller caller, TimeSpan position, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, _, c) =>
        {
            if (!s.Target.Capabilities.SupportsSeek) return;
            var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            await s.Target.SeekAsync(clamped, c);
            s.Position = clamped;
        }, ct);

    public Task<PlaybackStateSnapshot> SetVolumeAsync(
        Guid sessionId, PlaybackCaller caller, int percent, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, _, c) =>
        {
            if (!s.Target.Capabilities.SupportsVolume) return;
            // The device's own ceiling applies before its own clamp, so a device configured to
            // stay quiet cannot be made loud by asking for 100.
            var clamped = Math.Clamp(percent, 0, s.MaxVolumePercent);
            await s.Target.SetVolumeAsync(clamped, c);
            s.VolumePercent = clamped;
        }, ct);

    public Task<PlaybackStateSnapshot> SetRepeatAsync(
        Guid sessionId, PlaybackCaller caller, PlaybackRepeatMode mode, CancellationToken ct) =>
        MutateAsync(sessionId, caller, (s, _, _) =>
        {
            s.Queue.RepeatMode = mode;
            SchedulePreload(s);
            return Task.CompletedTask;
        }, ct);

    public Task<PlaybackStateSnapshot> SetShuffleAsync(
        Guid sessionId, PlaybackCaller caller, bool shuffled, CancellationToken ct) =>
        MutateAsync(sessionId, caller, (s, _, _) =>
        {
            s.Queue.SetShuffled(shuffled);
            SchedulePreload(s);
            return Task.CompletedTask;
        }, ct);

    public Task<PlaybackStateSnapshot> SetAutoPlayAsync(
        Guid sessionId, PlaybackCaller caller, bool enabled, CancellationToken ct) =>
        MutateAsync(sessionId, caller, async (s, sp, c) =>
        {
            s.AutoPlay = enabled;
            // Turning it on with a queue that is already nearly done should fill it now, not at
            // the end of the track that happens to be playing.
            if (enabled) await MaybeAutoPlayAsync(s, sp, c);
        }, ct);

    // ---- Auto-play ----

    /// <summary>
    /// Keep an auto-playing session's queue from running out, and from growing without end.
    /// <para>
    /// Called <em>before</em> each advance rather than after it: the moment the last track finishes
    /// is the moment the queue stops, and a top-up that arrived afterwards would be answering a
    /// speaker that had already gone quiet. In practice the threshold means it has usually run
    /// several tracks earlier, so the fetch is nowhere near the join.
    /// </para>
    /// <para>
    /// Every failure here is swallowed with a log line. Auto-play is a convenience layered over a
    /// queue that is otherwise fine, and a similarity query that fails is not a reason to take a
    /// working session down with it.
    /// </para>
    /// </summary>
    private async Task MaybeAutoPlayAsync(LiveSession session, IServiceProvider sp, CancellationToken ct)
    {
        if (!session.AutoPlay || session.Queue.Count == 0) return;
        // Repeat already means the queue never ends; there is nothing here to add to it.
        if (session.Queue.RepeatMode != PlaybackRepeatMode.Off) return;

        var remaining = session.Queue.Count - 1 - session.Queue.Index;
        if (remaining > AutoPlayRefillThreshold) return;

        var seeds = session.Played.Take(AutoPlaySeedCount).ToList();
        if (seeds.Count == 0 && session.Queue.CurrentTrackId is { } current) seeds.Add(current);
        if (seeds.Count == 0) return;

        // Everything queued and everything heard: a radio that circles back to the track from
        // twenty minutes ago is the failure mode this whole feature is judged on.
        var exclude = new HashSet<Guid>(session.Queue.Tracks);
        exclude.UnionWith(session.Played);

        IReadOnlyList<Track> batch;
        try
        {
            batch = await sp.GetRequiredService<ISimilarityService>()
                .GetAutoPlayQueueAsync(seeds, exclude, AutoPlayBatchSize, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Auto-play could not extend session {Session}", session.Id);
            return;
        }

        if (batch.Count == 0)
        {
            _log.LogInformation(
                "Auto-play found nothing else to play for session {Session}; the queue will end",
                session.Id);
            return;
        }

        var ids = batch.Select(t => t.Id).ToList();
        await LoadMetadataAsync(session, ids, sp, ct);
        foreach (var id in ids) session.AutoPlayed.Add(id);

        // Append only. Inserting would put the radio ahead of tracks the listener queued by hand.
        await ApplyAsync(session, session.Queue.Add(ids, null), sp, ct);
        SchedulePreload(session);

        foreach (var dropped in session.Queue.TrimHistory(AutoPlayHistoryLimit))
            session.AutoPlayed.Remove(dropped);

        _log.LogDebug(
            "Auto-play added {Count} tracks to session {Session}", ids.Count, session.Id);
    }

    // ---- The poll loop ----

    public async Task TickAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        // A session nobody is driving any more is not worth polling, and polling a hijacked
        // device would fight whoever took it.
        if (session.State is PlaybackSessionState.Hijacked or PlaybackSessionState.Offline) return;

        if (!await session.Gate.WaitAsync(TimeSpan.Zero, ct))
            return; // A command is in flight; it will leave the session in a fresher state anyway.

        try
        {
            var before = session.Snapshot(_time.GetUtcNow());

            TransportState state;
            try
            {
                state = await session.Target.PollAsync(ct);
                session.PollFailures = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                session.PollFailures++;
                if (session.PollFailures >= _options.OfflineAfterFailedPolls)
                {
                    _log.LogWarning(ex,
                        "Device {Device} stopped responding after {Count} polls; session {Session} kept for resume",
                        session.DeviceName, session.PollFailures, session.Id);
                    session.State = PlaybackSessionState.Offline;
                    await PublishStateAsync(session, session.Snapshot(_time.GetUtcNow()), ct);
                }
                return;
            }

            // Checked before hijack detection, because the URI a gapless join lands on is one
            // cosine handed the device itself and hijack detection would otherwise read it as a
            // stranger's.
            if (GaplessAdvanced(session, state))
            {
                await AdoptGaplessAdvanceAsync(session, state, ct);
                // Auto-play may have extended the queue on the way through.
                if (session.Queue.Version != before.QueueVersion) await PublishQueueAsync(session, ct);
                await PublishStateAsync(session, session.Snapshot(_time.GetUtcNow()), ct);
                await PersistAsync(session, force: true, ct);
                return;
            }

            if (DetectHijack(session, state))
            {
                _log.LogInformation(
                    "Device {Device} is playing something cosine did not set; session {Session} hijacked",
                    session.DeviceName, session.Id);
                session.State = PlaybackSessionState.Hijacked;
                await PublishStateAsync(session, session.Snapshot(_time.GetUtcNow()), ct);
                return;
            }

            session.Position = state.Position;
            if (state.Duration is { } reported && reported > TimeSpan.Zero) session.Duration = reported;
            if (state.VolumePercent is { } vol) session.VolumePercent = vol;
            if (state.State == PlaybackState.Playing) session.SawPlaying = true;

            // Before the advance, so the track that is ending is still the current one.
            await MaybeScrobbleAsync(session, ct);

            if (session.State == PlaybackSessionState.Playing && TrackFinished(session, state))
            {
                await using var scope = _scopes.CreateAsyncScope();
                await MaybeAutoPlayAsync(session, scope.ServiceProvider, ct);
                await ApplyAsync(session, session.Queue.TrackEnded(), scope.ServiceProvider, ct);
                // Repeat-one restarts the same track rather than loading another, so nothing else
                // would clear this - and a deliberate replay counts again, as it does locally.
                session.ScrobbledTrackId = null;
            }
            else if (state.State == PlaybackState.Paused && session.State == PlaybackSessionState.Playing)
            {
                // Paused at the device rather than through cosine - follow it, don't fight it.
                session.State = PlaybackSessionState.Paused;
            }

            var after = session.Snapshot(_time.GetUtcNow());
            // A queue that grew during the tick - auto-play's doing - is sent as a queue event of
            // its own. The version on the state snapshot would eventually make a client come and
            // ask for it, but making it ask is a round-trip for something already in hand.
            if (after.QueueVersion != before.QueueVersion)
            {
                await PublishQueueAsync(session, ct);
                // The queue is only persisted in full on a forced write, and an auto-play batch
                // that lived in memory until the next command would not survive a restart.
                await PersistAsync(session, force: true, ct);
            }
            if (StateChanged(before, after)) await PublishStateAsync(session, after, ct);
            await PersistAsync(session, force: false, ct);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>
    /// Submit the play once enough of the track has been heard, and report it as now-playing -
    /// the same two things the browser's <c>maybeScrobble</c> does through <c>/rest/scrobble</c>.
    /// Driven by the device's reported position rather than by loading the track, so skipping
    /// through a queue on a speaker no more records twenty plays than skipping through one in the
    /// browser does.
    /// </summary>
    private async Task MaybeScrobbleAsync(LiveSession session, CancellationToken ct)
    {
        if (session.State != PlaybackSessionState.Playing) return;
        if (session.Queue.CurrentTrackId is not { } trackId) return;
        if (session.ScrobbledTrackId == trackId) return;
        if (session.Duration is not { } total || total <= TimeSpan.Zero) return;

        var threshold = total * ScrobbleFraction < ScrobbleAfter ? total * ScrobbleFraction : ScrobbleAfter;
        if (session.Position < threshold) return;

        // Marked before the call, not after: a scrobble that fails is not worth retrying every
        // second for the rest of the track, and a duplicated play count is worse than a lost one.
        session.ScrobbledTrackId = trackId;

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var history = scope.ServiceProvider.GetRequiredService<IPlayHistoryService>();
            await history.RecordPlayAsync(session.OwnerUserId, trackId, ct);

            var nowPlaying = scope.ServiceProvider.GetRequiredService<INowPlayingService>();
            await nowPlaying.ReportAsync(
                session.OwnerUserId, session.OwnerUsername, trackId,
                (int)session.Position.TotalMilliseconds, INowPlayingService.StatePlaying, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Failed to record the play of {Track} for session {Session}", trackId, session.Id);
        }
    }

    /// <summary>
    /// A device is hijacked when it is playing a URI cosine did not give it - its own UI, another
    /// app, another cosine session. Only judged once cosine has actually set a URI, and never
    /// while the device reports nothing at all, since plenty of renderers report an empty URI
    /// between tracks.
    /// </summary>
    /// <remarks>
    /// The preload hint is a URI cosine gave the device, so a device that has moved onto it is
    /// not hijacked. That case never reaches here - <see cref="GaplessAdvanced"/> is checked
    /// first - but the two must be read together: for as long as they were not, a renderer making
    /// its own gapless join looked exactly like a stranger taking the speaker, and every session
    /// that lost that race stopped dead at the end of a track.
    /// </remarks>
    private static bool DetectHijack(LiveSession session, TransportState state)
    {
        if (session.LastSetUri is null) return false;
        if (string.IsNullOrEmpty(state.CurrentUri)) return false;
        return !string.Equals(state.CurrentUri, session.LastSetUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// The device has made the gapless join itself: it is reporting the URI cosine handed it as
    /// the next track, so the preload it was armed with has been consumed.
    /// </summary>
    private static bool GaplessAdvanced(LiveSession session, TransportState state) =>
        session.PreloadedUri is not null
        && string.Equals(state.CurrentUri, session.PreloadedUri, StringComparison.Ordinal);

    /// <summary>
    /// Follow a join the device made on its own, without commanding it: the audio is already
    /// playing and re-loading the same URL would restart it audibly.
    /// <para>
    /// This is the point of arming a preload at all. The alternative - noticing at the end of the
    /// track and issuing <c>SetAVTransportURI</c> - is a race against the renderer, and cosine
    /// only used to win it because <see cref="TrackFinished"/> fired a tolerance early, which cut
    /// the last second off every track. Losing it stranded the session.
    /// </para>
    /// </summary>
    private async Task AdoptGaplessAdvanceAsync(LiveSession session, TransportState state, CancellationToken ct)
    {
        var expected = session.PreloadedTrackId;
        var uri = session.PreloadedUri;
        var duration = session.PreloadedDuration;

        // Spent either way: the device consumed the hint when it made the join, and most
        // renderers clear their own next-URI slot at the same moment.
        session.ClearPreload();

        var move = session.Queue.TrackEnded();

        if (move.Action != QueueAction.Play || session.Queue.CurrentTrackId != expected)
        {
            // The queue changed after the hint was armed, so the device is playing a track the
            // queue no longer wants next. Command it onto the right one rather than follow it.
            _log.LogInformation(
                "Device {Device} advanced onto a preloaded track the queue has since moved past; " +
                "session {Session} is being put back on the queue's own next track",
                session.DeviceName, session.Id);
            await using var scope = _scopes.CreateAsyncScope();
            await ApplyAsync(session, move, scope.ServiceProvider, ct);
            return;
        }

        session.LoadedTrackId = expected;
        if (expected is { } joined) session.NotePlayed(joined);
        session.LastSetUri = uri;
        session.Duration = duration;
        session.Position = state.Position;
        session.ScrobbledTrackId = null;
        session.State = PlaybackSessionState.Playing;
        // The device is audibly on the new track, so end-of-track detection is armed from here -
        // unlike a fresh Load, which has to wait for the device to confirm.
        session.SawPlaying = state.State == PlaybackState.Playing;

        // A device that makes its own joins never reports the stop the tick's refill hangs off, so
        // without this an auto-playing session on a gapless renderer would coast to the end of the
        // queue and only then discover there was nothing more.
        if (session.AutoPlay)
        {
            await using var scope = _scopes.CreateAsyncScope();
            await MaybeAutoPlayAsync(session, scope.ServiceProvider, ct);
        }

        SchedulePreload(session);
    }

    /// <summary>
    /// End of track. A renderer reports Stopped both when a track finishes and while it is
    /// opening the next one, so this only counts once the device has actually been observed
    /// playing - otherwise the moment after <c>Load</c> would look like a finished track and the
    /// queue would race to its end.
    /// </summary>
    private bool TrackFinished(LiveSession session, TransportState state)
    {
        if (!session.SawPlaying) return false;
        if (state.State == PlaybackState.Stopped) return true;

        var duration = session.Duration;
        if (duration is null || duration <= TimeSpan.Zero) return false;

        var tolerance = TimeSpan.FromMilliseconds(_options.EndOfTrackToleranceMs);
        if (state.Position < duration.Value - tolerance) return false;

        // At the end, and still playing, on a device holding the next track: that device will
        // make the join itself and cosine will follow it in AdoptGaplessAdvanceAsync. Ending the
        // track here instead would cut the last second off it and race the renderer for the
        // handover - a race whose loser was a session frozen mid-queue.
        //
        // Anything else at the end of a track is finished. A renderer that parks on
        // PAUSED_PLAYBACK rather than STOPPED is the case worth spelling out: the branch below
        // would otherwise read it as a pause and sit there for good.
        return session.PreloadedUri is null || state.State != PlaybackState.Playing;
    }

    // ---- Shared plumbing ----

    private LiveSession Require(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : throw new PlaybackSessionNotFoundException(sessionId);

    /// <summary>
    /// Only the owner steers a session; an admin may also, so a forgotten session in an empty
    /// room can be dealt with.
    /// </summary>
    private static void Authorize(LiveSession session, PlaybackCaller caller)
    {
        if (session.OwnerUserId != caller.UserId && !caller.IsAdmin)
            throw new PlaybackNotOwnerException(session.Id);
    }

    /// <summary>
    /// Runs one command against a session with the session's lock held, then publishes and
    /// persists whatever changed. Every public transport method goes through here so that
    /// authorization, locking, publishing and persistence cannot be forgotten in one of them.
    /// </summary>
    private async Task<PlaybackStateSnapshot> MutateAsync(
        Guid sessionId, PlaybackCaller caller,
        Func<LiveSession, IServiceProvider, CancellationToken, Task> action,
        CancellationToken ct)
    {
        var session = Require(sessionId);
        Authorize(session, caller);

        await session.Gate.WaitAsync(ct);
        try
        {
            var beforeVersion = session.Queue.Version;
            await using var scope = _scopes.CreateAsyncScope();
            await action(session, scope.ServiceProvider, ct);

            var snapshot = session.Snapshot(_time.GetUtcNow());
            if (session.Queue.Version != beforeVersion) await PublishQueueAsync(session, ct);
            await PublishStateAsync(session, snapshot, ct);
            await PersistAsync(session, force: true, ct);
            return snapshot;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Carry out whatever the queue model decided.</summary>
    private async Task ApplyAsync(LiveSession session, QueueMove move, IServiceProvider sp, CancellationToken ct)
    {
        switch (move.Action)
        {
            case QueueAction.Play:
                await StartTrackAsync(session, move.Index, TimeSpan.Zero, sp, ct);
                break;

            case QueueAction.RestartCurrent:
                if (session.Target.Capabilities.SupportsSeek)
                {
                    await session.Target.SeekAsync(TimeSpan.Zero, ct);
                    await session.Target.PlayAsync(ct);
                    session.Position = TimeSpan.Zero;
                    session.State = PlaybackSessionState.Playing;
                }
                else
                {
                    // No seek: reload the same track, which is the only way back to zero.
                    await StartTrackAsync(session, session.Queue.Index, TimeSpan.Zero, sp, ct);
                }
                break;

            case QueueAction.Stop:
                await session.Target.StopAsync(ct);
                session.State = PlaybackSessionState.Stopped;
                session.Position = TimeSpan.Zero;
                session.LoadedTrackId = null;
                session.LastSetUri = null;
                await ClearPreloadAsync(session, ct);
                break;

            case QueueAction.None:
                break;
        }
    }

    private async Task StartTrackAsync(
        LiveSession session, int index, TimeSpan startAt, IServiceProvider sp, CancellationToken ct)
    {
        var resolver = sp.GetRequiredService<IPlaybackStreamResolver>();
        var trackId = session.Queue.Tracks[index];

        var presentation = await resolver.ResolveAsync(
            session.Id, trackId, session.OwnerUserId,
            session.Target.Capabilities, session.TranscodeProfile, ct);

        await session.Target.LoadAsync(presentation, startAt, ct);
        await session.Target.PlayAsync(ct);

        session.LoadedTrackId = trackId;
        session.NotePlayed(trackId);
        session.ScrobbledTrackId = null;
        session.LastSetUri = presentation.Url;
        session.Duration = presentation.Duration;
        session.Position = startAt;
        session.State = PlaybackSessionState.Playing;
        // The device has not confirmed it is playing yet, so end-of-track detection must not
        // fire until it does.
        session.SawPlaying = false;
        // Setting a current URI drops the next one on most renderers, and a hint cosine still
        // believed in would have it waiting for a join the device is no longer going to make.
        session.ClearPreload();

        SchedulePreload(session);
    }

    /// <summary>
    /// Take the hint back off the device as well as out of the session. A stopped queue that left
    /// a next-URI armed would have the renderer start playing again on its own.
    /// </summary>
    private static async Task ClearPreloadAsync(LiveSession session, CancellationToken ct)
    {
        if (!session.Target.Capabilities.SupportsPreload) return;

        // Bumped even when nothing is armed, because a hint still being resolved in the background
        // would otherwise land on the device after this and start it playing again on its own.
        Interlocked.Increment(ref session.PreloadGeneration);

        if (session.PreloadedTrackId is null && session.PreloadTargetTrackId is null) return;

        session.ClearPreload();
        await session.Target.PreloadNextAsync(null, ct);
    }

    /// <summary>
    /// Push the gapless hint, where the device takes one - off the caller's path, because nobody
    /// is waiting to hear the track it is about.
    /// <para>
    /// Resolving the next track is not cheap. On a device that requires a <c>Content-Length</c>
    /// the resolver materialises the whole transcode before it can answer with one, which is an
    /// entire ffmpeg encode - seconds. Done inline it ran with the session's gate held and before
    /// the command's own response was written, so the speaker was audibly playing the new track
    /// while the browser sat on a pending request still showing the old one. That is the reported
    /// lag, and it was longest exactly where transcoding was in the path.
    /// </para>
    /// <para>
    /// Only the hint is deferred. Everything a caller can observe - the queue, the index, the
    /// state - is already set by the time this is called, so the snapshot published without
    /// waiting for it is complete. What comes next is the device's business and the poll loop's.
    /// </para>
    /// <para>
    /// Must be called with the session's gate held: the queue is read here, synchronously, so that
    /// the background half never touches the queue model.
    /// </para>
    /// </summary>
    private void SchedulePreload(LiveSession session)
    {
        if (!session.Target.Capabilities.SupportsPreload) return;

        var upcoming = session.Queue.Upcoming();
        // Against the target rather than what the device holds, so a burst of queue edits that
        // don't change what comes next resolves once instead of once each.
        if (upcoming == session.PreloadTargetTrackId) return;

        session.PreloadTargetTrackId = upcoming;
        var generation = Interlocked.Increment(ref session.PreloadGeneration);
        session.PreloadWork = ArmPreloadAsync(session, upcoming, generation);
    }

    /// <summary>
    /// The deferred half of <see cref="SchedulePreload"/>: resolve the track, then hand it over.
    /// <para>
    /// The resolve deliberately happens outside the gate. It is the slow part and it touches
    /// nothing the gate protects - only the call that puts the URI on the device has to be
    /// serialised against commands and polls, and that is one SOAP round trip. Holding the gate
    /// across the encode would have moved the stall rather than removed it: every button pressed
    /// during it would have queued behind it.
    /// </para>
    /// <para>
    /// The URI is kept alongside the track id because it is what the device will report once it
    /// makes the join, and comparing against it is how that join is told apart from a hijack.
    /// </para>
    /// </summary>
    private async Task ArmPreloadAsync(LiveSession session, Guid? upcoming, int generation)
    {
        try
        {
            var ct = session.Lifetime.Token;

            TrackPresentation? next = null;
            if (upcoming is { } nextId)
            {
                await using var scope = _scopes.CreateAsyncScope();
                var resolver = scope.ServiceProvider.GetRequiredService<IPlaybackStreamResolver>();
                next = await resolver.ResolveAsync(
                    session.Id, nextId, session.OwnerUserId,
                    session.Target.Capabilities, session.TranscodeProfile, ct);
            }

            await session.Gate.WaitAsync(ct);
            try
            {
                // Superseded while we were resolving, or while we waited for the gate. A later
                // move knows better than this one what should be armed, and it is on its way.
                if (Volatile.Read(ref session.PreloadGeneration) != generation) return;

                await session.Target.PreloadNextAsync(next, ct);
                session.PreloadedTrackId = upcoming;
                session.PreloadedUri = next?.Url;
                session.PreloadedDuration = next?.Duration;
            }
            finally
            {
                session.Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended while this was in flight. Nothing to arm, nothing to say.
        }
        catch (Exception ex)
        {
            // A hint that could not be armed costs a gapless join, not the session: the poll loop
            // still ends the track and loads the next one itself. Put the target back to what the
            // device actually holds so the next queue move tries again rather than deciding this
            // one already covered it.
            if (Volatile.Read(ref session.PreloadGeneration) == generation)
                session.PreloadTargetTrackId = session.PreloadedTrackId;

            _log.LogWarning(ex,
                "Could not arm the next track on {Device} for session {Session}",
                session.DeviceName, session.Id);
        }
    }

    /// <summary>
    /// Completes once the hint this session last scheduled has been armed on the device - or
    /// abandoned. Arming is deliberately off the command path, which leaves "the device is holding
    /// the next track" something that becomes true shortly after a command rather than as part of
    /// it. Nothing in the running system needs to know when, because the poll loop reads the
    /// session rather than the device; a test asserting against a simulated device does, and
    /// waiting on a timer instead would be a flake.
    /// </summary>
    public Task WaitForPreloadAsync(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.PreloadWork : Task.CompletedTask;

    private static async Task LoadMetadataAsync(
        LiveSession session, IReadOnlyList<Guid> trackIds, IServiceProvider sp, CancellationToken ct)
    {
        var missing = trackIds.Distinct().Where(id => !session.Meta.ContainsKey(id)).ToList();
        if (missing.Count == 0) return;

        var db = sp.GetRequiredService<CosineDbContext>();
        var rows = await db.Tracks.AsNoTracking()
            .Where(t => missing.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.Title,
                Artist = t.Artist.Name,
                t.ArtistId,
                Album = t.Album.Name,
                t.AlbumId,
                t.DurationMs,
                t.Format,
                t.BitRate,
                // The full artist credit, so a speaker's queue names - and links - the guests
                // the browser's does. ArtistId stays the primary artist.
                Credits = t.TrackArtists
                    .OrderBy(ta => ta.Ordinal)
                    .Select(ta => new { ta.CreditedAs, ta.JoinPhrase, ta.ArtistId, ArtistName = ta.Artist.Name })
                    .ToList(),
            })
            .ToListAsync(ct);

        foreach (var r in rows)
        {
            session.Meta[r.Id] = new PlaybackQueueItem(
                Position: 0,
                TrackId: r.Id,
                Title: r.Title,
                Artist: Services.ArtistCreditParsing.Display(
                    r.Credits.Select(c => (c.CreditedAs, c.JoinPhrase)).ToList(), r.Artist),
                ArtistId: r.ArtistId,
                Album: r.Album,
                AlbumId: r.AlbumId,
                DurationMs: r.DurationMs ?? 0,
                Suffix: r.Format,
                BitRate: r.BitRate,
                Artists: r.Credits.Count > 0
                    ? r.Credits.Select(c => new PlaybackQueueArtist(c.ArtistId, c.ArtistName)).ToList()
                    : null);
        }
    }

    private static bool StateChanged(PlaybackStateSnapshot a, PlaybackStateSnapshot b) =>
        a with { ServerTimestamp = default, PositionMs = 0 } != b with { ServerTimestamp = default, PositionMs = 0 }
        || Math.Abs(a.PositionMs - b.PositionMs) >= 250;

    private async Task PublishStateAsync(LiveSession session, PlaybackStateSnapshot snapshot, CancellationToken ct)
    {
        session.LastPublished = snapshot;
        await _hub.Clients.Group(PlaybackHub.SessionGroup(session.Id))
            .SendAsync("PlaybackState", snapshot, ct);
    }

    private async Task PublishQueueAsync(LiveSession session, CancellationToken ct) =>
        await _hub.Clients.Group(PlaybackHub.SessionGroup(session.Id))
            .SendAsync("QueueChanged", session.QueueSnapshot(), ct);

    private async Task PersistAsync(LiveSession session, bool force, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!force && now - session.LastPersistedAt < PositionPersistInterval) return;
        session.LastPersistedAt = now;

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var row = await db.PlaybackSessions
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == session.Id, ct);
        if (row is null) return;

        row.State = session.State;
        row.CurrentIndex = session.Queue.Index;
        row.PositionMs = (long)session.Position.TotalMilliseconds;
        row.VolumePercent = session.VolumePercent;
        row.RepeatMode = session.Queue.RepeatMode;
        row.Shuffle = session.Queue.Shuffled;
        row.UpdatedAt = now;

        if (force && !QueueRowsMatch(row, session.Queue.Tracks))
        {
            db.PlaybackSessionTracks.RemoveRange(row.Tracks);
            for (var i = 0; i < session.Queue.Tracks.Count; i++)
            {
                db.PlaybackSessionTracks.Add(new PlaybackSessionTrack
                {
                    Id = Guid.NewGuid(),
                    SessionId = row.Id,
                    Position = i,
                    TrackId = session.Queue.Tracks[i],
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool QueueRowsMatch(PlaybackSession row, IReadOnlyList<Guid> tracks)
    {
        if (row.Tracks.Count != tracks.Count) return false;
        return row.Tracks.OrderBy(t => t.Position).Select(t => t.TrackId).SequenceEqual(tracks);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.Lifetime.CancelAsync();
                await session.PreloadWork;
                session.Lifetime.Dispose();
                await session.Target.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Disposing target for session {Session}", session.Id);
            }
        }

        _sessions.Clear();
    }

    /// <summary>
    /// The live half of a session - the parts that cannot be persisted: the connected device, the
    /// queue model, and the poll bookkeeping.
    /// </summary>
    private sealed class LiveSession(
        Guid id, Guid deviceId, string deviceName, string? transcodeProfile,
        Guid ownerUserId, string ownerUsername, IPlaybackTarget target)
    {
        public Guid Id { get; } = id;
        public Guid DeviceId { get; } = deviceId;

        // Settable, not fixed at connect time: an admin editing a device is expected to change
        // what is playing on it now, not only what plays on it next time.
        public string DeviceName { get; set; } = deviceName;
        public string? TranscodeProfile { get; set; } = transcodeProfile;

        public Guid OwnerUserId { get; } = ownerUserId;
        public string OwnerUsername { get; } = ownerUsername;
        public IPlaybackTarget Target { get; } = target;

        /// <summary>Serialises commands against the poll loop; a device takes one thing at a time.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public PlaybackQueueModel Queue { get; } = new();
        public Dictionary<Guid, PlaybackQueueItem> Meta { get; } = [];

        /// <summary>
        /// Whether the session tops its own queue up (see <c>MaybeAutoPlayAsync</c>). Deliberately
        /// not persisted to <c>PlaybackSessions</c>: nothing restores a live session from that row
        /// - <c>StartSessionAsync</c> deletes any it finds from a previous process - so a column
        /// for this would be written and never read.
        /// </summary>
        public bool AutoPlay { get; set; }

        /// <summary>
        /// Track ids this session has actually played, most recent first and capped. Both the
        /// seeds for the next auto-play batch and the "don't play it again" list come from here
        /// rather than from queue order, because under shuffle the entries before the index are
        /// not the ones that were just heard.
        /// </summary>
        public List<Guid> Played { get; } = [];

        /// <summary>Ids auto-play put in the queue, so the queue panel can mark them.</summary>
        public HashSet<Guid> AutoPlayed { get; } = [];

        public void NotePlayed(Guid trackId)
        {
            Played.Remove(trackId);
            Played.Insert(0, trackId);
            if (Played.Count > PlayedLogLimit) Played.RemoveRange(PlayedLogLimit, Played.Count - PlayedLogLimit);
        }

        public PlaybackSessionState State { get; set; } = PlaybackSessionState.Idle;
        public TimeSpan Position { get; set; }
        public TimeSpan? Duration { get; set; }
        public int VolumePercent { get; set; } = 100;
        public int MaxVolumePercent { get; set; } = 100;

        /// <summary>What cosine last told the device to play, for hijack detection.</summary>
        public string? LastSetUri { get; set; }

        public Guid? LoadedTrackId { get; set; }

        /// <summary>What the device is holding. See <see cref="PreloadTargetTrackId"/>.</summary>
        public Guid? PreloadedTrackId { get; set; }

        /// <summary>
        /// What the device <em>should</em> be holding, as of the last queue move. Arming is done
        /// in the background, so for as long as an encode is running the two differ; this is the
        /// one to ask "does the hint still need re-arming?", and it is what keeps a burst of queue
        /// edits from resolving the same track once each.
        /// </summary>
        public Guid? PreloadTargetTrackId { get; set; }

        /// <summary>
        /// The URI behind <see cref="PreloadedTrackId"/>. A device that joins onto it gaplessly
        /// reports it as its current URI, and that is the only way to tell cosine's own preload
        /// working from somebody else taking the speaker.
        /// </summary>
        public string? PreloadedUri { get; set; }

        /// <summary>
        /// Length of the preloaded track, kept because a gapless join is adopted without going
        /// back to the resolver and the scrubber would otherwise keep the old track's length.
        /// </summary>
        public TimeSpan? PreloadedDuration { get; set; }

        /// <summary>
        /// Bumped by every decision about what should be armed. A background arm carries the value
        /// it was scheduled with and does nothing if it no longer matches, which is how a hint that
        /// took an encode to resolve is stopped from landing on a device the queue has since moved
        /// on - or stopped.
        /// </summary>
        public int PreloadGeneration;

        /// <summary>The arm currently in flight, for shutdown and for tests to wait on.</summary>
        public Task PreloadWork { get; set; } = Task.CompletedTask;

        /// <summary>
        /// Cancelled when the session ends. Background arming can be as long as an ffmpeg encode,
        /// and finishing one for a session that no longer exists would both waste the work and
        /// race the target's disposal.
        /// </summary>
        public CancellationTokenSource Lifetime { get; } = new();

        /// <summary>Forget the hint, in one place so no half of it is left behind.</summary>
        public void ClearPreload()
        {
            PreloadedTrackId = null;
            PreloadTargetTrackId = null;
            PreloadedUri = null;
            PreloadedDuration = null;
        }

        /// <summary>
        /// Guards the play count against firing more than once per track start. Cleared whenever a
        /// track is loaded and when one ends, so a replay counts again.
        /// </summary>
        public Guid? ScrobbledTrackId { get; set; }

        /// <summary>
        /// Whether the device has confirmed it is playing the current track. End-of-track
        /// detection depends on it, so a Stopped report while the device is still opening the
        /// stream cannot be mistaken for a finished track.
        /// </summary>
        public bool SawPlaying { get; set; }

        public int PollFailures { get; set; }
        public DateTimeOffset LastPersistedAt { get; set; }
        public PlaybackStateSnapshot? LastPublished { get; set; }

        public PlaybackStateSnapshot Snapshot(DateTimeOffset now) => new(
            Id, DeviceId, DeviceName, State, Queue.Index, Queue.CurrentTrackId,
            (long)Position.TotalMilliseconds,
            Duration is { } d ? (long)d.TotalMilliseconds : null,
            VolumePercent, Queue.RepeatMode, Queue.Shuffled, Queue.Version,
            OwnerUserId, OwnerUsername, now, AutoPlay);

        public PlaybackQueueSnapshot QueueSnapshot()
        {
            var items = new List<PlaybackQueueItem>(Queue.Count);
            for (var i = 0; i < Queue.Tracks.Count; i++)
            {
                var trackId = Queue.Tracks[i];
                var auto = AutoPlayed.Contains(trackId);
                items.Add(Meta.TryGetValue(trackId, out var meta)
                    ? meta with { Position = i, AutoPlay = auto }
                    : new PlaybackQueueItem(
                        i, trackId, string.Empty, string.Empty, Guid.Empty,
                        string.Empty, Guid.Empty, 0, null, null, auto));
            }

            return new PlaybackQueueSnapshot(Id, Queue.Version, items);
        }
    }
}
