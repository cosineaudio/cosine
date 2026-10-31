using Cosine.Core.Interfaces;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Bounds how many transcodes run at once.
///
/// A slot is taken before ffmpeg starts and released when the listener's stream is disposed -
/// i.e. it is held for the whole song, not for the CPU work. ffmpeg blocks on the stdout pipe
/// as soon as the client's buffer fills, so a held slot mostly costs a process and a pipe. That
/// makes the core count the wrong limit (it capped concurrent *listeners* at 8 on an 8-core
/// box) and it makes an unbounded wait the wrong queue: the caller past the limit used to block
/// until some unrelated listener stopped, with no timeout and no feedback. The wait is bounded
/// now and a full server says so.
/// </summary>
public sealed class TranscodeSlots : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate;

    public TranscodeSlots(int limit, TimeSpan timeout)
    {
        Limit = limit > 0 ? limit : AutoLimit();
        Timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
        _gate = new SemaphoreSlim(Limit, Limit);
    }

    public TranscodeSlots(int configuredLimit, int configuredTimeoutSeconds)
        : this(configuredLimit, TimeSpan.FromSeconds(Math.Max(configuredTimeoutSeconds, 0)))
    {
    }

    public int Limit { get; }
    public TimeSpan Timeout { get; }

    /// <summary>Slots not currently held. Exposed for tests and diagnostics.</summary>
    public int Available => _gate.CurrentCount;

    /// <summary>
    /// Slots held right now - i.e. transcodes in flight. A slot is held for the whole listen,
    /// so this is "how many people are on a transcoded stream", not "how many ffmpeg processes
    /// are burning CPU this instant".
    /// </summary>
    public int Active => Limit - _gate.CurrentCount;

    private static int AutoLimit() => Math.Max(4, Environment.ProcessorCount * 4);

    /// <summary>
    /// Takes a slot, waiting at most <see cref="Timeout"/>.
    /// </summary>
    /// <exception cref="TranscodeCapacityException">All slots were busy for the whole wait.</exception>
    public async Task AcquireAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(Timeout, ct))
            throw new TranscodeCapacityException(Limit, Timeout);
    }

    public void Release() => _gate.Release();

    public void Dispose() => _gate.Dispose();
}
