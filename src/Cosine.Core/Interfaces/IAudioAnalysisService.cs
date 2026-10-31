namespace Cosine.Core.Interfaces;

/// <summary>
/// Content-based analysis of the audio files themselves: decode an excerpt, extract a feature
/// vector, store it. Backs the audio half of <see cref="ISimilarityService"/>.
/// <para>
/// Every method is a no-op when <see cref="IVectorSupport.IsAvailable"/> is false.
/// </para>
/// </summary>
public interface IAudioAnalysisService
{
    /// <summary>
    /// Analyses every track that has no features yet or was analysed by an older analyzer
    /// version, reporting progress to admins as it goes. Resumable: interrupting it loses only
    /// the tracks in flight, and re-running picks up where it stopped.
    /// </summary>
    /// <returns>
    /// How many tracks were analysed, and how many could not be. Failures are returned rather
    /// than only logged because the caller owns the end of the pass - it is what reports the
    /// result to the admin panel, and it cannot report a failure count it was never told.
    /// </returns>
    Task<(int Analyzed, int Failed)> AnalyzeMissingAsync(CancellationToken ct = default);

    /// <summary>
    /// Analyses one track and stores the result, replacing any existing row.
    /// </summary>
    /// <returns>False when the file is missing, undecodable, or analysis is unavailable.</returns>
    Task<bool> AnalyzeTrackAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>How many tracks have a current-version analysis, and how many exist in total.</summary>
    Task<(int Analyzed, int Total)> GetCoverageAsync(CancellationToken ct = default);
}
