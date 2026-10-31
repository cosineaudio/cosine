using Pgvector;

namespace Cosine.Core.Entities;

/// <summary>
/// One track's content-based audio analysis.
/// <para>
/// This table only exists where the <c>vector</c> extension is installed - the migration that
/// creates it is guarded - so <b>nothing may query it without first checking
/// <c>IVectorSupport.IsAvailable</c></b>.
/// </para>
/// </summary>
public class TrackAudioFeature
{
    public Guid TrackId { get; set; }

    /// <summary>
    /// The <c>AudioAnalyzer.FeatureVersion</c> that produced <see cref="Raw"/>. The analysis job
    /// re-analyses any row below the current version, so bumping the analyzer rolls the library
    /// forward on its own.
    /// </summary>
    public int FeatureVersion { get; set; }

    public DateTimeOffset AnalyzedAt { get; set; }

    /// <summary>How much audio was analysed - an excerpt, not usually the whole track.</summary>
    public long AnalyzedMs { get; set; }

    /// <summary>
    /// The analyzer's direct output, never re-derived. Kept because z-scoring needs library-wide
    /// statistics that change as the library grows: re-normalizing is a pass over this column,
    /// not a re-decode of every file.
    /// </summary>
    public Vector Raw { get; set; } = null!;

    /// <summary>
    /// <see cref="Raw"/> z-scored per dimension and scaled per facet by <c>sqrt(weight)</c>.
    /// This is the indexed column and the only one similarity queries touch. Null until the
    /// normalization pass has run over a library large enough to have meaningful statistics.
    /// </summary>
    public Vector? Normalized { get; set; }

    /// <summary>
    /// Mean and standard deviation of this track's distance to a random sample of the library,
    /// which is all Mutual Proximity needs to rescore a candidate set and stop a handful of
    /// tracks from turning up in everybody's radio. Null until the same pass has run.
    /// </summary>
    public double? DistanceMean { get; set; }

    public double? DistanceStdDev { get; set; }

    // Readable scalars. They fall out of the same analysis pass, cost nothing extra, and are
    // deliberately not part of the similarity vector - see the plan.
    public double Bpm { get; set; }
    public int KeyRoot { get; set; }
    public int KeyMode { get; set; }
    public double KeyStrength { get; set; }
    public double LoudnessDb { get; set; }

    public Track Track { get; set; } = null!;
}
