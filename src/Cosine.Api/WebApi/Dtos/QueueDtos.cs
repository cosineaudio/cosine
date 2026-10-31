namespace Cosine.Api.WebApi.Dtos;

/// <summary>The caller's saved play queue, restored on app start.</summary>
public sealed record QueueDto(
    IReadOnlyList<TrackDto> Tracks,
    Guid? CurrentTrackId,
    long? PositionMs);

/// <summary>
/// Replaces the saved queue wholesale. An empty <see cref="TrackIds"/> clears it. The current
/// track is named by id (its first occurrence in the list); Subsonic's repeated-query-param
/// encoding is gone.
/// </summary>
public sealed record SaveQueueRequest(
    IReadOnlyList<Guid> TrackIds,
    Guid? CurrentTrackId = null,
    long? PositionMs = null);
