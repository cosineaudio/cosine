namespace Cosine.Api.WebApi.Dtos;

public class PlaylistSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public string? Owner { get; set; }
    public bool IsPublic { get; set; }

    /// <summary>
    /// System-managed playlists (Release Radar) are materialized by a job, rendered
    /// distinctly, and cannot be edited or deleted.
    /// </summary>
    public bool IsSystem { get; set; }

    public int TrackCount { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One entry of a playlist. <see cref="EntryId"/> is the entry's own stable id - what
/// <c>DELETE /api/playlists/{id}/tracks/{entryId}</c> takes. Unlike the Subsonic surface's
/// position index it cannot drift when the playlist changes underneath the caller.
/// </summary>
public sealed record PlaylistEntryDto(Guid EntryId, TrackDto Track);

public sealed class PlaylistDetailDto : PlaylistSummaryDto
{
    public IReadOnlyList<PlaylistEntryDto> Entries { get; set; } = [];
}

public sealed record PlaylistListResponse(IReadOnlyList<PlaylistSummaryDto> Playlists);

public sealed record CreatePlaylistRequest(string Name, IReadOnlyList<Guid>? TrackIds = null);

/// <summary>PATCH semantics: only non-null fields change.</summary>
public sealed record UpdatePlaylistRequest(string? Name = null, string? Comment = null, bool? IsPublic = null);

public sealed record AddPlaylistTracksRequest(IReadOnlyList<Guid> TrackIds);
