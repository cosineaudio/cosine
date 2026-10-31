namespace Cosine.Core.Options;

public class AudioAnalysisOptions
{
    public const string Section = "AudioAnalysis";

    /// <summary>
    /// How many tracks are decoded and analysed at once. Analysis is a background sweep over the
    /// whole library and playback is not - the default is deliberately a fraction of the core
    /// count so a first-run analysis cannot starve live transcoding. 0 = auto.
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// Rows written per database round trip. This governs write batching only - progress
    /// reporting used to be tied to it, which made how often an operator heard anything a
    /// side effect of how often Postgres was written to.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// How often the sweep writes a progress line. Frequent enough to see a stall inside a
    /// minute, sparse enough that a six-hour run does not bury everything else in the log.
    /// </summary>
    public int ProgressLogSeconds { get; set; } = 20;

    /// <summary>
    /// How often progress is pushed to the admin panel. This is a UI refresh rate, not a log
    /// level - twice a second is what makes a bar look like it is moving rather than stepping.
    /// </summary>
    public int ProgressEventMilliseconds { get; set; } = 500;

    public TimeSpan ProgressLogInterval => TimeSpan.FromSeconds(Math.Max(1, ProgressLogSeconds));

    public TimeSpan ProgressEventInterval
        => TimeSpan.FromMilliseconds(Math.Max(50, ProgressEventMilliseconds));

    /// <summary>
    /// Set false to stop the scan queueing analysis. Analysis already stored stays queryable;
    /// this only governs whether new work is picked up.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int ResolvedConcurrency => MaxConcurrency > 0
        ? MaxConcurrency
        : Math.Max(1, Environment.ProcessorCount / 2);
}
