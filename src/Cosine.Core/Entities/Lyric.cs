namespace Cosine.Core.Entities;

// A single set of lyrics for a track. A track can have several: multiple
// languages, or a synced sidecar plus an unsynced fallback. Sidecar rows are
// reconciled against .lrc/.elrc files on disk; embedded rows come from tags and
// are only kept when the track has no sidecar (see ScanJob).
public class Lyric
{
    public const string SourceSidecar = "sidecar";
    public const string SourceEmbedded = "embedded";

    public Guid Id { get; set; }
    public Guid TrackId { get; set; }

    // SourceSidecar | SourceEmbedded
    public string Source { get; set; } = SourceSidecar;

    // Library-relative path of the .lrc/.elrc file (null for embedded lyrics).
    public string? SourcePath { get; set; }

    // ISO 639 code when known; "xxx" (tagger convention for "unspecified") otherwise.
    public string Lang { get; set; } = "xxx";
    public bool Synced { get; set; }

    // LRC [offset:] tag, in milliseconds. Positive means lyrics appear sooner.
    public int? OffsetMs { get; set; }
    public string? DisplayArtist { get; set; }
    public string? DisplayTitle { get; set; }

    // JSON array of { start?: int (ms), value: string }, ordered by start (synced)
    // or appearance (unsynced). Stored as jsonb.
    public string LinesJson { get; set; } = "[]";

    // Newline-joined line text, served by the legacy getLyrics endpoint.
    public string PlainText { get; set; } = string.Empty;

    // Sidecar change-detection; null for embedded lyrics.
    public long? SourceSize { get; set; }
    public DateTimeOffset? SourceModifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Track Track { get; set; } = null!;
}
