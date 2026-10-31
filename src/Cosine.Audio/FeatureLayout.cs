namespace Cosine.Audio;

/// <summary>
/// Where each facet lives inside the flat feature vector, and how strongly it counts.
/// <para>
/// The layout is public because normalization (which z-scores per dimension and then scales
/// each facet block) needs it as much as extraction does. The scaling trick it enables:
/// once every block is z-scored and multiplied by <c>sqrt(weight)</c>, the <b>squared L2
/// distance over the whole concatenated vector is exactly the weighted sum of the per-facet
/// squared distances</b>. That is what lets a single pgvector column with one HNSW index
/// serve a tunable multi-facet similarity - and why the query path must use L2 (<c>&lt;-&gt;</c>)
/// and never cosine, which renormalizes globally and destroys the additivity.
/// </para>
/// <para>
/// Re-weighting is therefore a rescale of stored vectors, not a re-analysis of the library.
/// </para>
/// </summary>
public static class FeatureLayout
{
    /// <summary>A facet's span in the vector, with the weight its block is scaled by.</summary>
    public readonly record struct Facet(string Name, int Offset, int Length, double Weight);

    // 19 MFCCs (C0 dropped - it is loudness, and the signal is level-normalized anyway),
    // as mean, std, delta-mean and delta-std.
    public const int TimbreOffset = 0;
    public const int TimbreLength = 76;

    // centroid, spread, rolloff85, flatness, ZCR, and 6 spectral-contrast bands: mean + std.
    public const int SpectralOffset = TimbreOffset + TimbreLength;
    public const int SpectralLength = 22;

    // 12-bin chroma rotated so index 0 is the estimated tonic: mean + std.
    public const int TonalOffset = SpectralOffset + SpectralLength;
    public const int TonalLength = 24;

    // Fluctuation pattern: 12 bands x 15 modulation-frequency bins spanning 0-10 Hz.
    public const int RhythmBands = 12;
    public const int RhythmBins = 15;
    public const int RhythmOffset = TonalOffset + TonalLength;
    public const int RhythmLength = RhythmBands * RhythmBins;

    // RMS coefficient of variation, crest factor, dynamic complexity, loudness range,
    // low-energy rate. All ratios or differences, so all level-invariant.
    public const int DynamicsOffset = RhythmOffset + RhythmLength;
    public const int DynamicsLength = 5;

    /// <summary>Total width of the feature vector.</summary>
    public const int Dimensions = DynamicsOffset + DynamicsLength;

    /// <summary>
    /// Default facet weights. Timbre leads because production and instrumentation dominate
    /// perceived similarity; rhythm is close behind because it is what separates two tracks
    /// that share a palette but not a feel. These are defaults, not constants - Phase 3
    /// reads them from configuration so they can be retuned without re-analyzing anything.
    /// </summary>
    public static readonly IReadOnlyList<Facet> Facets =
    [
        new("timbre",   TimbreOffset,   TimbreLength,   1.0),
        new("rhythm",   RhythmOffset,   RhythmLength,   0.8),
        new("tonal",    TonalOffset,    TonalLength,    0.5),
        new("spectral", SpectralOffset, SpectralLength, 0.4),
        new("dynamics", DynamicsOffset, DynamicsLength, 0.2),
    ];
}
