namespace Cosine.Core.Interfaces;

/// <summary>
/// The latest playback report for one user. Deliberately not persisted: "now playing" is
/// live state, worthless a restart later, and writing a row per progress report would put
/// the whole fleet's playback traffic through the database.
/// </summary>
public record NowPlayingReport(
    Guid UserId,
    string Username,
    Guid TrackId,
    int? PositionMs,
    string State,
    DateTimeOffset ReportedAt);

/// <summary>
/// Tracks what each user is currently listening to. Fed by <c>scrobble</c> and
/// <c>reportPlayback</c>, read by <c>getNowPlaying</c> (filtered to the caller), and pushed
/// over <c>ActivityHub</c> to the owning user's connections only - playback is private to
/// each user and is never shown to other accounts.
/// </summary>
public interface INowPlayingService
{
    /// <summary>States that keep an entry alive; anything else clears the user's entry.</summary>
    public const string StatePlaying = "playing";
    public const string StatePaused = "paused";
    public const string StateStopped = "stopped";

    Task ReportAsync(
        Guid userId, string username, Guid trackId,
        int? positionMs = null, string state = StatePlaying, CancellationToken ct = default);

    /// <summary>Current entries, most recently reported first. Expired ones are dropped here.</summary>
    IReadOnlyList<NowPlayingReport> GetCurrent();
}
