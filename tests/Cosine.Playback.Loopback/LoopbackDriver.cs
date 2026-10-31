using System.Runtime.CompilerServices;
using Cosine.Playback.Abstractions;

namespace Cosine.Playback.Loopback;

/// <summary>
/// A driver for devices that do not exist. It exposes the fake devices named in
/// <see cref="LoopbackOptions.DeviceNames"/> and connects to them as
/// <see cref="LoopbackTarget"/>s, so the engine, the advance logic, the stream tokens and the
/// hub are testable with no hardware and no network (plan §5, Phase 1).
/// <para>
/// Nothing here opens an audio device or decodes anything - cosine never plays audio locally
/// (plan §1.1). This project is referenced only by <c>Cosine.Tests</c> and registered through
/// the test host's DI: it is not in the shipped application and has no configuration switch,
/// so there is no production code path that exists only to be turned off (plan §4.6).
/// </para>
/// </summary>
public sealed class LoopbackDriver : IPlaybackDriver
{
    /// <summary>The value persisted on device rows as <c>DriverId</c>.</summary>
    public const string Id = "loopback";

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<LoopbackTarget> _connected = new();

    /// <param name="options">Which devices to expose and how they behave.</param>
    /// <param name="timeProvider">
    /// Clock handed to every target this driver connects. Defaults to
    /// <see cref="TimeProvider.System"/>.
    /// </param>
    public LoopbackDriver(LoopbackOptions? options = null, TimeProvider? timeProvider = null)
    {
        Options = options ?? new LoopbackOptions();
        Options.Validate();
        _time = timeProvider ?? TimeProvider.System;

        Devices = Options.DeviceNames
            .Select((name, index) => new DiscoveredDevice(
                DriverId: Id,
                DeviceKey: $"loopback-{index}",
                DisplayName: name,
                Address: "loopback",
                Model: "cosine simulated device"))
            .ToArray();
    }

    /// <inheritdoc />
    public string DriverId => Id;

    /// <summary>The options this driver was built with.</summary>
    public LoopbackOptions Options { get; }

    /// <summary>
    /// The devices this driver will report, in discovery order. Exposed so a test can connect to
    /// one without first running discovery.
    /// </summary>
    public IReadOnlyList<DiscoveredDevice> Devices { get; }

    /// <summary>
    /// Every target handed out by <see cref="ConnectAsync"/>, oldest first. The engine connects
    /// its own targets and never surfaces them, so this is how a test reaches the
    /// <see cref="LoopbackTarget"/> it needs to inspect or to fail.
    /// </summary>
    public IReadOnlyList<LoopbackTarget> ConnectedTargets
    {
        get { lock (_gate) { return _connected.ToArray(); } }
    }

    /// <summary>The most recently connected target, or null if nothing has connected yet.</summary>
    public LoopbackTarget? LastTarget
    {
        get { lock (_gate) { return _connected.Count == 0 ? null : _connected[^1]; } }
    }

    /// <summary>
    /// Yields the configured devices and then stays open until the token is cancelled, exactly
    /// as a real SSDP or mDNS listener does - the contract says discovery keeps running so a
    /// device powered on later still shows up, and a driver that returned as soon as it ran out
    /// of devices would be lying about that. Cancellation ends the sequence cleanly: the
    /// enumeration finishes rather than throwing, so an <c>await foreach</c> over it simply
    /// stops.
    /// </summary>
    public async IAsyncEnumerable<DiscoveredDevice> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var device in Devices)
        {
            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            yield return device;
        }

        await WaitForCancellationAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IPlaybackTarget> ConnectAsync(DiscoveredDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ct.ThrowIfCancellationRequested();

        if (!string.Equals(device.DriverId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Device '{device.DeviceKey}' belongs to driver '{device.DriverId}', not '{Id}'.",
                nameof(device));
        }

        if (!Devices.Any(d => string.Equals(d.DeviceKey, device.DeviceKey, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"No loopback device with key '{device.DeviceKey}'. This driver exposes: " +
                $"{string.Join(", ", Devices.Select(d => d.DeviceKey))}.",
                nameof(device));
        }

        var target = new LoopbackTarget(device, Options, _time);
        lock (_gate)
        {
            _connected.Add(target);
        }

        return Task.FromResult<IPlaybackTarget>(target);
    }

    /// <summary>
    /// Completes when the token is cancelled, without throwing. No timer and no polling, so it
    /// costs nothing while a discovery loop sits open for the life of a test.
    /// </summary>
    private static async Task WaitForCancellationAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), completion);

        await completion.Task.ConfigureAwait(false);
    }
}
