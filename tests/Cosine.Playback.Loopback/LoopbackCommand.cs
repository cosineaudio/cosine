using Cosine.Playback.Abstractions;

namespace Cosine.Playback.Loopback;

/// <summary>Which <see cref="IPlaybackTarget"/> method was called.</summary>
public enum LoopbackCommandKind
{
    Load,
    Play,
    Pause,
    Stop,
    Seek,
    SetVolume,
    PreloadNext,
    Dispose,
}

/// <summary>
/// One command the engine sent to a <see cref="LoopbackTarget"/>. The ordered log of these is
/// how a test asserts what the engine *did* - "it preloaded the next track before the current
/// one ended" - rather than only what state it ended up in.
/// <para>
/// <see cref="IPlaybackTarget.PollAsync"/> is deliberately absent: it is an observation, not a
/// command, and at 1 Hz it would bury the log. <see cref="LoopbackTarget.PollCount"/> covers
/// the cases where the number of polls matters.
/// </para>
/// </summary>
/// <param name="Kind">Which method was called.</param>
/// <param name="At">Simulated wall-clock time of the call, from the target's TimeProvider.</param>
/// <param name="Track">The track argument for Load and PreloadNext; null otherwise (and null
/// for a PreloadNext that cleared the hint).</param>
/// <param name="Position">The requested position for Load's <c>startAt</c> and for Seek.</param>
/// <param name="VolumePercent">The value SetVolume was *asked* for, before clamping - so a
/// test can see that the engine sent 150 even though the device applied 100.</param>
/// <param name="Ignored">
/// True when the device accepted the call but did nothing with it: a seek or preload or volume
/// change the declared <see cref="PlaybackCapabilities"/> say it cannot do, or a pause while
/// already stopped. The call is still logged, because "the engine asked" is the interesting
/// fact.
/// </param>
public sealed record LoopbackCommand(
    LoopbackCommandKind Kind,
    DateTimeOffset At,
    TrackPresentation? Track = null,
    TimeSpan? Position = null,
    int? VolumePercent = null,
    bool Ignored = false);
