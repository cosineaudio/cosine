namespace Cosine.Core.Interfaces;

/// <summary>
/// Whether the connected PostgreSQL actually has the <c>vector</c> extension installed.
/// <para>
/// cosine's own compose file ships <c>pgvector/pgvector:pg17</c>, so this is true for anyone
/// running the stack as published. Self-hosters pointing cosine at a PostgreSQL they already
/// run may have stock server packages with no pgvector available; the audio-similarity
/// migration is written to no-op there rather than fail, and this flag is how every consumer
/// finds out. When false, audio analysis never schedules and similarity falls back to the
/// metadata-only scoring in <c>SimilarityScoring</c> - a less clever radio, not a broken server.
/// </para>
/// </summary>
public interface IVectorSupport
{
    /// <summary>
    /// True once the extension has been confirmed present. Resolved once at startup, after
    /// migrations have run, so reading it is free.
    /// </summary>
    bool IsAvailable { get; }
}
