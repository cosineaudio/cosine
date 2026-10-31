using Cosine.Core.Dtos;

namespace Cosine.Core.Interfaces;

public interface ILyricsService
{
    /// <summary>All stored lyric sets for a track, synced sets first. Cached in Redis.</summary>
    Task<IReadOnlyList<StructuredLyricDto>> GetStructuredLyricsAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>Best-effort plain-text lyrics for a track matched by title (and artist, if given).</summary>
    Task<PlainLyricDto?> GetPlainLyricsAsync(string? artist, string? title, CancellationToken ct = default);
}
