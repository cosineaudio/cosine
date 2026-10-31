namespace Cosine.Core.Interfaces;

/// <summary>
/// The second pass over stored analysis: z-score every raw vector against library-wide
/// statistics, apply facet weights, and estimate the per-track distance distribution that
/// Mutual Proximity needs. Runs after analysis, and again whenever the library has grown enough
/// that the statistics have drifted.
/// </summary>
public interface IAudioNormalizationService
{
    /// <summary>
    /// Recomputes normalization statistics and rewrites every track's normalized vector and
    /// distance statistics.
    /// </summary>
    /// <returns>How many tracks were normalized.</returns>
    Task<int> NormalizeAsync(CancellationToken ct = default);

    /// <summary>
    /// How many analysed tracks are still missing a normalized vector - the work a call to
    /// <see cref="NormalizeAsync"/> would pick up.
    /// </summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);
}
