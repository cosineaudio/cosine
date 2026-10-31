using Cosine.Playback.Abstractions;

namespace Cosine.Playback.Loopback;

/// <summary>
/// Configuration for the simulated device (plan §4.6). Every knob here describes the *device
/// being simulated* - how many of them there are, what they claim they can do, and how fast
/// their clock runs - so a test can point the engine at a device that behaves like the awkward
/// hardware it will meet in the field.
/// </summary>
public sealed record LoopbackOptions
{
    /// <summary>
    /// A device that can do everything: the baseline the engine is written against, so a test
    /// that overrides <see cref="Capabilities"/> is visibly testing a degraded device.
    /// </summary>
    public static readonly PlaybackCapabilities CapableDevice = new(
        SupportsSeek: true,
        SupportsPreload: true,
        SupportsVolume: true,
        RequiresContentLength: false,
        Mode: TransportMode.Pull,
        Codecs: new[] { "audio/flac", "audio/mpeg" });

    /// <summary>
    /// One entry per fake device to expose, in discovery order - so the count is the number of
    /// devices and the values are their names. Empty is legal and means discovery finds
    /// nothing, which is itself worth testing.
    /// <para>
    /// <see cref="DiscoveredDevice.DeviceKey"/> is derived from the position in this list
    /// (<c>loopback-0</c>, <c>loopback-1</c>, …), so it is stable across rediscovery and across
    /// driver instances built from the same options - which is what
    /// <c>(DriverId, DeviceKey)</c> reconciliation requires.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DeviceNames { get; init; } = new[] { "Loopback Speaker" };

    /// <summary>
    /// What the fake devices report from <see cref="IPlaybackTarget.Capabilities"/>. Override
    /// to exercise the engine against a device that cannot seek, cannot preload, or has no
    /// volume control. Defaults to <see cref="CapableDevice"/>.
    /// </summary>
    public PlaybackCapabilities Capabilities { get; init; } = CapableDevice;

    /// <summary>
    /// How fast the simulated transport clock runs relative to wall-clock time. 1.0 is
    /// real time; 60.0 means a three-minute track finishes in three seconds.
    /// <para>
    /// This is configuration of a simulated device, not a hidden test hook: a device whose
    /// clock is a number rather than a crystal may as well let you choose the number, and the
    /// alternative - an integration test that watches a three-track queue auto-advance in real
    /// time - takes ten minutes to prove something that takes one second to prove here.
    /// Unit tests should still inject a fake <see cref="TimeProvider"/> and leave this at 1.0;
    /// the multiplier is for integration tests that want a real, running clock, just a faster
    /// one.
    /// </para>
    /// </summary>
    public double ClockRate { get; init; } = 1.0;

    /// <summary>Volume the device reports before anyone sets it. Clamped to 0–100.</summary>
    public int InitialVolumePercent { get; init; } = 50;

    /// <summary>
    /// <paramref name="count"/> devices named "<paramref name="namePrefix"/> 1", "… 2", and so
    /// on - the common case of "give me three speakers" without hand-writing the names.
    /// </summary>
    public static LoopbackOptions WithDevices(int count, string namePrefix = "Loopback Speaker")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new LoopbackOptions
        {
            DeviceNames = Enumerable.Range(1, count).Select(i => $"{namePrefix} {i}").ToArray()
        };
    }

    /// <summary>
    /// Throws if the options describe a device that could not exist. Called by
    /// <see cref="LoopbackDriver"/> at construction so a bad clock rate fails at wiring time
    /// rather than as a silently frozen transport.
    /// </summary>
    public void Validate()
    {
        if (DeviceNames is null)
        {
            throw new ArgumentException($"{nameof(DeviceNames)} must not be null.", nameof(DeviceNames));
        }

        if (Capabilities is null)
        {
            throw new ArgumentException($"{nameof(Capabilities)} must not be null.", nameof(Capabilities));
        }

        if (!double.IsFinite(ClockRate) || ClockRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ClockRate), ClockRate, "Clock rate must be a finite number greater than zero.");
        }
    }
}
