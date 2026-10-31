using System.IO;
using Cosine.Playback.Abstractions;

namespace Cosine.Playback.Loopback;

/// <summary>
/// A simulated transport. It accepts every command the engine can send, keeps a position that
/// advances with wall-clock time while it says it is playing, and reports that back through
/// <see cref="PollAsync"/>.
/// <para>
/// It opens no audio device, decodes nothing, and touches no sound API - there is no local
/// playback in cosine (plan §1.1). "Playing" here means one field says Playing and a clock is
/// running; nothing is audible anywhere.
/// </para>
/// <para>
/// It also does not advance the queue at the end of a track. It stops, pins the position at the
/// duration, and waits. Advancing is the server's job (plan §4.5), and letting the server's
/// advance logic be tested is the entire reason this type exists.
/// </para>
/// </summary>
public sealed class LoopbackTarget : IPlaybackTarget
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly double _clockRate;
    private readonly List<LoopbackCommand> _commands = new();

    private TrackPresentation? _loaded;
    private TrackPresentation? _preloaded;
    private PlaybackState _state = PlaybackState.Stopped;
    private TimeSpan _position;
    private long _anchor;
    private int _volume;
    private string? _currentUri;
    private long _pollCount;
    private Exception? _failure;
    private bool _disposed;

    /// <param name="device">The device this target is connected to.</param>
    /// <param name="options">Capabilities and clock rate of the simulated device.</param>
    /// <param name="timeProvider">
    /// The clock the position is measured against. Defaults to <see cref="TimeProvider.System"/>;
    /// unit tests pass a fake so the simulated transport is deterministic and instant.
    /// </param>
    public LoopbackTarget(DiscoveredDevice device, LoopbackOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        Device = device;
        Options = options ?? new LoopbackOptions();
        Options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _clockRate = Options.ClockRate;
        _volume = Math.Clamp(Options.InitialVolumePercent, 0, 100);
        _anchor = _time.GetTimestamp();
    }

    /// <summary>The device this target was connected to.</summary>
    public DiscoveredDevice Device { get; }

    /// <summary>The options this target was built with.</summary>
    public LoopbackOptions Options { get; }

    /// <inheritdoc />
    public PlaybackCapabilities Capabilities => Options.Capabilities;

    /// <summary>
    /// Every command the engine has sent, in order. See <see cref="LoopbackCommand"/> for why
    /// polls are not in here.
    /// </summary>
    public IReadOnlyList<LoopbackCommand> Commands
    {
        get { lock (_gate) { return _commands.ToArray(); } }
    }

    /// <summary>Current transport state, with the simulated clock brought up to date.</summary>
    public PlaybackState State
    {
        get { lock (_gate) { AdvanceLocked(); return _state; } }
    }

    /// <summary>Current position, with the simulated clock brought up to date.</summary>
    public TimeSpan Position
    {
        get { lock (_gate) { AdvanceLocked(); return _position; } }
    }

    /// <summary>The track most recently handed to <see cref="LoadAsync"/>, if any.</summary>
    public TrackPresentation? LoadedTrack
    {
        get { lock (_gate) { return _loaded; } }
    }

    /// <summary>
    /// The track most recently handed to <see cref="PreloadNextAsync"/>. Always null on a device
    /// whose <see cref="PlaybackCapabilities.SupportsPreload"/> is false - the hint is dropped,
    /// though the call still shows up in <see cref="Commands"/>.
    /// </summary>
    public TrackPresentation? PreloadedTrack
    {
        get { lock (_gate) { return _preloaded; } }
    }

    /// <summary>
    /// What the device says it is playing: the loaded track's URL, or whatever
    /// <see cref="SimulateHijack"/> last set.
    /// </summary>
    public string? CurrentUri
    {
        get { lock (_gate) { return _currentUri; } }
    }

    /// <summary>Volume the device actually applied, after clamping.</summary>
    public int VolumePercent
    {
        get { lock (_gate) { return _volume; } }
    }

    /// <summary>How many times the engine has polled.</summary>
    public long PollCount
    {
        get { lock (_gate) { return _pollCount; } }
    }

    /// <summary>True once <see cref="DisposeAsync"/> has run.</summary>
    public bool IsDisposed
    {
        get { lock (_gate) { return _disposed; } }
    }

    /// <summary>
    /// True once <see cref="SimulateDeviceLost"/> has been called and not undone.
    /// </summary>
    public bool IsLost
    {
        get { lock (_gate) { return _failure is not null; } }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sets the URI, parks the transport at <see cref="PlaybackState.Stopped"/>, and leaves any
    /// preload hint alone - clearing that is the engine's job via
    /// <see cref="PreloadNextAsync"/>(null). On a device that cannot seek, <paramref name="startAt"/>
    /// is ignored and playback starts from zero, as the interface says it must.
    /// </remarks>
    public Task LoadAsync(TrackPresentation track, TimeSpan startAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_gate)
        {
            EnsureUsableLocked(ct);

            var start = Capabilities.SupportsSeek ? Clamp(startAt, track.Duration) : TimeSpan.Zero;
            _loaded = track;
            _currentUri = track.Url;
            _position = start;
            _state = PlaybackState.Stopped;
            _anchor = _time.GetTimestamp();
            RecordLocked(LoopbackCommandKind.Load, track: track, position: startAt);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Throws <see cref="InvalidOperationException"/> when nothing is loaded - a real renderer
    /// faults on Play with no URI set, and silently swallowing it would hide an engine bug this
    /// driver exists to catch.
    /// </remarks>
    public Task PlayAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureUsableLocked(ct);

            if (_loaded is null)
            {
                throw new InvalidOperationException(
                    $"Loopback device '{Device.DisplayName}' was told to play with nothing loaded.");
            }

            if (_state != PlaybackState.Playing)
            {
                _anchor = _time.GetTimestamp();
                _state = PlaybackState.Playing;
            }

            // Play at the end of a track stops again immediately rather than replaying it.
            AdvanceLocked();
            RecordLocked(LoopbackCommandKind.Play);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Pause while not playing is recorded as ignored and changes nothing.</remarks>
    public Task PauseAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureUsableLocked(ct);
            AdvanceLocked();

            var ignored = _state != PlaybackState.Playing;
            if (!ignored)
            {
                _state = PlaybackState.Paused;
            }

            RecordLocked(LoopbackCommandKind.Pause, ignored: ignored);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Stop rewinds to zero, as a renderer's Stop does. The URI stays set.</remarks>
    public Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureUsableLocked(ct);
            AdvanceLocked();
            _state = PlaybackState.Stopped;
            _position = TimeSpan.Zero;
            _anchor = _time.GetTimestamp();
            RecordLocked(LoopbackCommandKind.Stop);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A no-op - recorded with <see cref="LoopbackCommand.Ignored"/> set - when
    /// <see cref="PlaybackCapabilities.SupportsSeek"/> is false. Not a throw: the interface says
    /// a device that cannot seek "starts from zero regardless" for
    /// <see cref="LoadAsync"/>'s <c>startAt</c>, volume clamps rather than throws, and preload is
    /// documented as a no-op when unsupported - unsupported capabilities are absorbed by the
    /// driver everywhere else, so seek behaves the same way. Position is clamped to
    /// 0–duration rather than throwing.
    /// </remarks>
    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureUsableLocked(ct);
            AdvanceLocked();

            var ignored = !Capabilities.SupportsSeek;
            if (!ignored)
            {
                _position = Clamp(position, _loaded?.Duration ?? TimeSpan.Zero);
                _anchor = _time.GetTimestamp();
                AdvanceLocked();
            }

            RecordLocked(LoopbackCommandKind.Seek, position: position, ignored: ignored);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Clamps to 0–100 rather than throwing, per the interface. The log keeps the value that was
    /// asked for; <see cref="VolumePercent"/> holds what the device applied.
    /// </remarks>
    public Task SetVolumeAsync(int percent, CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureUsableLocked(ct);

            var ignored = !Capabilities.SupportsVolume;
            if (!ignored)
            {
                _volume = Math.Clamp(percent, 0, 100);
            }

            RecordLocked(LoopbackCommandKind.SetVolume, volumePercent: percent, ignored: ignored);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PreloadNextAsync(TrackPresentation? next, CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureUsableLocked(ct);

            var ignored = !Capabilities.SupportsPreload;
            if (!ignored)
            {
                _preloaded = next;
            }

            RecordLocked(LoopbackCommandKind.PreloadNext, track: next, ignored: ignored);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reports <see cref="PlaybackState.Stopped"/> with the position pinned at the duration once
    /// the track has run out, and keeps reporting exactly that: the position never wraps and
    /// never counts past the end. Throws once <see cref="SimulateDeviceLost"/> has been called,
    /// which is how the interface says a target reports the device has gone away.
    /// </remarks>
    public Task<TransportState> PollAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            EnsureUsableLocked(ct);
            AdvanceLocked();
            _pollCount++;

            return Task.FromResult(new TransportState(
                State: _state,
                Position: _position,
                Duration: _loaded?.Duration,
                VolumePercent: Capabilities.SupportsVolume ? _volume : null,
                CurrentUri: _currentUri));
        }
    }

    /// <summary>
    /// Releases the (nonexistent) connection. Idempotent. Every subsequent
    /// <see cref="IPlaybackTarget"/> call throws <see cref="ObjectDisposedException"/> - using a
    /// disposed target is a bug in the caller, and a driver that quietly no-ops would let the
    /// engine keep "commanding" a target it has already thrown away. The inspection properties
    /// (<see cref="Commands"/>, <see cref="Position"/>, …) stay readable so a test can assert on
    /// the corpse.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            AdvanceLocked();
            _state = PlaybackState.Stopped;
            _disposed = true;
            RecordLocked(LoopbackCommandKind.Dispose);
        }

        return ValueTask.CompletedTask;
    }

    // ---- Test-double controls -------------------------------------------------------------
    // Deliberately on the concrete type and not on IPlaybackTarget: nothing outside a test ever
    // holds a LoopbackTarget, so this is where a simulation control belongs (plan §4.6).

    /// <summary>
    /// Someone else grabbed the device - the renderer's own UI, Spotify Connect, another cosine
    /// session. Sets <see cref="CurrentUri"/> to a URI cosine did not set and leaves the rest of
    /// the transport alone, so the engine's hijack detection sees exactly what it would see in
    /// the field.
    /// </summary>
    public void SimulateHijack(string foreignUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignUri);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _currentUri = foreignUri;
        }
    }

    /// <summary>
    /// The renderer made the gapless join itself: the track it was handed by
    /// <see cref="PreloadNextAsync"/> becomes the one it is playing, from zero, and the next-URI
    /// slot empties. This is what every renderer that implements <c>SetNextAVTransportURI</c>
    /// does at the end of a track, and doing it on command rather than automatically is what lets
    /// a test place the join exactly where the engine's end-of-track rules are interesting.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is preloaded, so there is no join to make.</exception>
    public void SimulateGaplessAdvance()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_preloaded is null)
            {
                throw new InvalidOperationException(
                    $"Loopback device '{Device.DisplayName}' has nothing preloaded to advance onto.");
            }

            _loaded = _preloaded;
            _preloaded = null;
            _currentUri = _loaded.Url;
            _position = TimeSpan.Zero;
            _state = PlaybackState.Playing;
            _anchor = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// The device went away - unplugged, rebooted, off the network. Every subsequent transport
    /// call and every <see cref="PollAsync"/> throws <paramref name="failure"/> (an
    /// <see cref="IOException"/> by default). <see cref="DisposeAsync"/> keeps working, because
    /// teardown of a dead session must not itself fail.
    /// </summary>
    public void SimulateDeviceLost(Exception? failure = null)
    {
        lock (_gate)
        {
            _failure = failure ?? new IOException(
                $"Loopback device '{Device.DisplayName}' is not responding.");
        }
    }

    /// <summary>The device came back. Clears <see cref="SimulateDeviceLost"/>.</summary>
    public void SimulateDeviceRestored()
    {
        lock (_gate)
        {
            _failure = null;
        }
    }

    /// <summary>
    /// Empties <see cref="Commands"/>, for a test that wants to assert on what happened after
    /// some setup rather than since construction.
    /// </summary>
    public void ClearCommands()
    {
        lock (_gate)
        {
            _commands.Clear();
        }
    }

    // ---- internals ------------------------------------------------------------------------

    /// <summary>
    /// Brings the simulated position up to now. Everything observable goes through here, so the
    /// position is a function of the clock rather than of how often anyone looked.
    /// </summary>
    private void AdvanceLocked()
    {
        if (_state != PlaybackState.Playing)
        {
            return;
        }

        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_anchor, now) * _clockRate;
        _anchor = now;

        if (elapsed > TimeSpan.Zero)
        {
            _position += elapsed;
        }

        var duration = _loaded?.Duration ?? TimeSpan.Zero;
        if (_position >= duration)
        {
            // End of track: stop dead at the duration. No wrap, no overrun, no self-advance.
            _position = duration;
            _state = PlaybackState.Stopped;
        }
    }

    private void EnsureUsableLocked(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_failure is not null)
        {
            throw _failure;
        }
    }

    private void RecordLocked(
        LoopbackCommandKind kind,
        TrackPresentation? track = null,
        TimeSpan? position = null,
        int? volumePercent = null,
        bool ignored = false)
        => _commands.Add(new LoopbackCommand(kind, _time.GetUtcNow(), track, position, volumePercent, ignored));

    private static TimeSpan Clamp(TimeSpan value, TimeSpan max)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > max ? max : value;
    }
}
