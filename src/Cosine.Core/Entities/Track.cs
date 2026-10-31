namespace Cosine.Core.Entities;

public class Track
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public Guid AlbumId { get; set; }
    public Guid ArtistId { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public long? DurationMs { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public int? BitRate { get; set; }
    public string? Format { get; set; }
    public string? ContentType { get; set; }
    public int? Year { get; set; }
    /// <summary>
    /// Track identity across scans. Prefers MUSICBRAINZ_RELEASETRACKID (the track's id
    /// *within a release*), which is what distinguishes two files of the same recording on
    /// two different releases - see <see cref="MusicBrainzRecordingId"/> for the other one.
    /// </summary>
    public string? MusicBrainzId { get; set; }

    /// <summary>
    /// MUSICBRAINZ_TRACKID - the *recording* id, despite the tag's name. This is the id
    /// MusicBrainz's <c>/ws/2/recording/</c> endpoint takes, and the only one that resolves
    /// to a per-track artist credit; <see cref="MusicBrainzId"/> is a release-track id, which
    /// has no lookup endpoint at all. Both are stored because they answer different questions.
    /// </summary>
    public string? MusicBrainzRecordingId { get; set; }

    public DateTimeOffset? FileModifiedAt { get; set; }
    public int StarCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Album Album { get; set; } = null!;
    public Artist Artist { get; set; } = null!;
    public ICollection<TrackArtist> TrackArtists { get; set; } = new List<TrackArtist>();
    public ICollection<TrackGenre> TrackGenres { get; set; } = new List<TrackGenre>();
    public ICollection<PlayHistory> PlayHistory { get; set; } = new List<PlayHistory>();
}
