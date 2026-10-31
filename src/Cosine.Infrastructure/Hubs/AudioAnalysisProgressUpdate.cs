namespace Cosine.Infrastructure.Hubs;

/// <summary>
/// One <c>AudioAnalysisProgress</c> push. Sent several times a second while a pass runs, so the
/// admin panel can show a bar that actually moves rather than one that jumps once a batch.
/// <para>
/// It is a record rather than a positional argument list because the shape has grown past the
/// point where the client can be trusted to keep the order straight, and because adding a field
/// to it must not silently shift the meaning of the ones already there.
/// </para>
/// </summary>
/// <param name="Phase">
/// Which half of the job is running - <c>analysing</c> or <c>normalizing</c>. A pass is not
/// finished when analysis ends: the vectors are unusable until normalization has run over them,
/// and a UI that stops reporting at that point looks stalled for the several minutes it takes.
/// </param>
/// <param name="Step">
/// What that phase is currently doing, in words meant for a human. Normalization spends most of
/// its time in steps that have no per-track counter, and "computing distance statistics" is a
/// far better answer to "is it stuck?" than a bar sitting at 100%.
/// </param>
/// <param name="Processed">Items finished in this phase, or 0 when the step has no count.</param>
/// <param name="Total">Items this phase will process, or 0 when the step has no count.</param>
/// <param name="Failed">Tracks that could not be analysed so far. They stay pending for the next run.</param>
/// <param name="TracksPerMinute">Recent throughput, smoothed. 0 before the first interval elapses.</param>
/// <param name="EtaSeconds">Estimated time to the end of this phase, or null when not yet known.</param>
/// <param name="CurrentTrack">The most recently finished track, so the panel shows movement between counts.</param>
public sealed record AudioAnalysisProgressUpdate(
    string Phase,
    string Step,
    int Processed,
    int Total,
    int Failed,
    double TracksPerMinute,
    int? EtaSeconds,
    string? CurrentTrack)
{
    public const string AnalysingPhase = "analysing";
    public const string NormalizingPhase = "normalizing";

    /// <summary>A step with no per-item count of its own - a spinner, not a bar.</summary>
    public static AudioAnalysisProgressUpdate Indeterminate(string phase, string step)
        => new(phase, step, 0, 0, 0, 0, null, null);
}
