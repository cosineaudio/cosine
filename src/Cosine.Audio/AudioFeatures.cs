using Cosine.Audio.Dsp;

namespace Cosine.Audio;

/// <summary>
/// One track's analysis result: the similarity vector, plus a handful of readable scalars that
/// fall out of the same pass and are worth storing as ordinary columns.
/// </summary>
/// <param name="Vector">
/// Raw, un-normalized feature vector of <see cref="FeatureLayout.Dimensions"/> values. "Raw"
/// matters: z-scoring needs library-wide statistics that do not exist yet at extraction time,
/// so normalization is a later pass over stored vectors. This array is never re-derived.
/// </param>
/// <param name="Bpm">Estimated tempo, or 0 when no periodicity was found. Metadata only.</param>
/// <param name="KeyRoot">Estimated tonic, 0 = C through 11 = B.</param>
/// <param name="Mode">Estimated mode.</param>
/// <param name="KeyStrength">
/// Correlation of the winning key profile, in [-1, 1]. Doubles as a confidence - percussive or
/// atonal material scores near zero.
/// </param>
/// <param name="LoudnessDb">
/// Level of the analysed excerpt in dBFS, measured <b>before</b> the level normalization that
/// the rest of the analysis runs on. Metadata only, deliberately not part of the vector.
/// </param>
/// <param name="AnalyzedMs">How much audio was actually analysed.</param>
public sealed record AudioFeatures(
    float[] Vector,
    double Bpm,
    int KeyRoot,
    KeyMode Mode,
    double KeyStrength,
    double LoudnessDb,
    long AnalyzedMs);
