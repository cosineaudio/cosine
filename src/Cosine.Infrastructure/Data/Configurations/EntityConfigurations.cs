using Cosine.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cosine.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Username).IsRequired().HasMaxLength(256);
        builder.HasIndex(u => u.Username).IsUnique();
        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.SubsonicSecret).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(256);
        builder.Property(u => u.IsAdmin).HasDefaultValue(false);
    }
}

public class ArtistConfiguration : IEntityTypeConfiguration<Artist>
{
    public void Configure(EntityTypeBuilder<Artist> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(512);
        builder.Property(a => a.SortName).IsRequired().HasMaxLength(512);
        builder.HasIndex(a => a.SortName);
        builder.Property(a => a.MusicBrainzId).HasMaxLength(64);
        builder.HasIndex(a => a.MusicBrainzId);
        builder.Property(a => a.StarCount).HasDefaultValue(0);
    }
}

public class AlbumConfiguration : IEntityTypeConfiguration<Album>
{
    public void Configure(EntityTypeBuilder<Album> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(512);
        builder.Property(a => a.SortName).IsRequired().HasMaxLength(512);
        builder.HasIndex(a => a.SortName);
        builder.HasIndex(a => a.CreatedAt).IsDescending();
        builder.HasIndex(a => a.ArtistId);
        builder.HasOne(a => a.Artist).WithMany(ar => ar.Albums).HasForeignKey(a => a.ArtistId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(a => a.MusicBrainzId).HasMaxLength(64);
        builder.HasIndex(a => a.MusicBrainzId);
        builder.Property(a => a.ReleaseType).HasMaxLength(32);
        builder.Property(a => a.DurationMs).HasDefaultValue(0L);
        builder.Property(a => a.TrackCount).HasDefaultValue(0);
        builder.Property(a => a.StarCount).HasDefaultValue(0);
    }
}

public class TrackConfiguration : IEntityTypeConfiguration<Track>
{
    public void Configure(EntityTypeBuilder<Track> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Title).IsRequired().HasMaxLength(512);
        builder.Property(t => t.SortTitle).IsRequired().HasMaxLength(512);
        builder.HasIndex(t => t.SortTitle);
        builder.HasIndex(t => t.CreatedAt).IsDescending();
        builder.HasIndex(t => t.AlbumId);
        builder.HasIndex(t => t.ArtistId);
        builder.HasIndex(t => t.FilePath);
        builder.Property(t => t.MusicBrainzId).HasMaxLength(64);
        builder.HasIndex(t => t.MusicBrainzId);
        builder.Property(t => t.MusicBrainzRecordingId).HasMaxLength(64);
        // The credit enrichment matches MusicBrainz recordings back onto tracks by this id.
        builder.HasIndex(t => t.MusicBrainzRecordingId);
        builder.HasOne(t => t.Album).WithMany(a => a.Tracks).HasForeignKey(t => t.AlbumId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(t => t.Artist).WithMany(a => a.Tracks).HasForeignKey(t => t.ArtistId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(t => t.FilePath).IsRequired().HasMaxLength(1024);
        builder.Property(t => t.Format).HasMaxLength(16);
        builder.Property(t => t.ContentType).HasMaxLength(64);
        builder.Property(t => t.StarCount).HasDefaultValue(0);
    }
}

public class LyricConfiguration : IEntityTypeConfiguration<Lyric>
{
    public void Configure(EntityTypeBuilder<Lyric> builder)
    {
        builder.HasKey(l => l.Id);
        builder.HasIndex(l => l.TrackId);
        // Sidecar reconciliation looks rows up by their file path.
        builder.HasIndex(l => l.SourcePath);
        builder.Property(l => l.Source).IsRequired().HasMaxLength(16);
        builder.Property(l => l.SourcePath).HasMaxLength(1024);
        builder.Property(l => l.Lang).IsRequired().HasMaxLength(16);
        builder.Property(l => l.DisplayArtist).HasMaxLength(512);
        builder.Property(l => l.DisplayTitle).HasMaxLength(512);
        builder.Property(l => l.LinesJson).HasColumnType("jsonb");
        builder.HasOne(l => l.Track).WithMany().HasForeignKey(l => l.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TrackArtistConfiguration : IEntityTypeConfiguration<TrackArtist>
{
    public void Configure(EntityTypeBuilder<TrackArtist> builder)
    {
        builder.HasKey(ta => new { ta.TrackId, ta.ArtistId });
        builder.Property(ta => ta.CreditedAs).IsRequired().HasMaxLength(512);
        builder.Property(ta => ta.JoinPhrase).IsRequired().HasMaxLength(64);
        builder.Property(ta => ta.Source).IsRequired().HasMaxLength(16).HasDefaultValue(TrackArtistSource.Tag);
        // Reading a track's credit is "all rows for this track, in ordinal order"; reading an
        // artist's guest appearances is the ArtistId side. Both are hot enough to index.
        builder.HasIndex(ta => new { ta.TrackId, ta.Ordinal });
        builder.HasIndex(ta => ta.ArtistId);
        builder.HasOne(ta => ta.Track).WithMany(t => t.TrackArtists).HasForeignKey(ta => ta.TrackId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(ta => ta.Artist).WithMany(a => a.TrackArtists).HasForeignKey(ta => ta.ArtistId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class GenreConfiguration : IEntityTypeConfiguration<Genre>
{
    public void Configure(EntityTypeBuilder<Genre> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).IsRequired().HasMaxLength(128);
        builder.HasIndex(g => g.Name).IsUnique();
    }
}

public class TrackGenreConfiguration : IEntityTypeConfiguration<TrackGenre>
{
    public void Configure(EntityTypeBuilder<TrackGenre> builder)
    {
        builder.HasKey(tg => new { tg.TrackId, tg.GenreId });
        builder.HasOne(tg => tg.Track).WithMany(t => t.TrackGenres).HasForeignKey(tg => tg.TrackId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(tg => tg.Genre).WithMany(g => g.TrackGenres).HasForeignKey(tg => tg.GenreId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AlbumGenreConfiguration : IEntityTypeConfiguration<AlbumGenre>
{
    public void Configure(EntityTypeBuilder<AlbumGenre> builder)
    {
        builder.HasKey(ag => new { ag.AlbumId, ag.GenreId });
        builder.HasOne(ag => ag.Album).WithMany(a => a.AlbumGenres).HasForeignKey(ag => ag.AlbumId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(ag => ag.Genre).WithMany(g => g.AlbumGenres).HasForeignKey(ag => ag.GenreId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ArtistGenreConfiguration : IEntityTypeConfiguration<ArtistGenre>
{
    public void Configure(EntityTypeBuilder<ArtistGenre> builder)
    {
        builder.HasKey(ag => new { ag.ArtistId, ag.GenreId });
        builder.HasOne(ag => ag.Artist).WithMany(a => a.ArtistGenres).HasForeignKey(ag => ag.ArtistId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(ag => ag.Genre).WithMany(g => g.ArtistGenres).HasForeignKey(ag => ag.GenreId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PlayHistoryConfiguration : IEntityTypeConfiguration<PlayHistory>
{
    public void Configure(EntityTypeBuilder<PlayHistory> builder)
    {
        builder.HasKey(ph => ph.Id);
        builder.HasIndex(ph => new { ph.UserId, ph.PlayedAt }).IsDescending(false, true);
        builder.HasIndex(ph => ph.TrackId);
        builder.HasOne(ph => ph.User).WithMany(u => u.PlayHistory).HasForeignKey(ph => ph.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(ph => ph.Track).WithMany(t => t.PlayHistory).HasForeignKey(ph => ph.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(128);
        builder.Property(s => s.Value).IsRequired();
    }
}

public class SearchIndexConfiguration : IEntityTypeConfiguration<SearchIndex>
{
    public void Configure(EntityTypeBuilder<SearchIndex> builder)
    {
        builder.HasKey(si => new { si.EntityType, si.EntityId });
        builder.Property(si => si.EntityType).HasMaxLength(16);
        builder.HasIndex(si => si.TextVector).HasMethod("GIN");
        builder.HasIndex(si => si.LyricsVector).HasMethod("GIN");
    }
}

public class PlaylistConfiguration : IEntityTypeConfiguration<Playlist>
{
    public void Configure(EntityTypeBuilder<Playlist> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(256);
        builder.HasOne(p => p.Owner).WithMany(u => u.Playlists).HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(p => p.IsSystem).HasDefaultValue(false);
        builder.HasIndex(p => new { p.OwnerId, p.IsSystem });
        builder.Property(p => p.TrackCount).HasDefaultValue(0);
        builder.Property(p => p.DurationMs).HasDefaultValue(0L);
    }
}

public class PlaylistTrackConfiguration : IEntityTypeConfiguration<PlaylistTrack>
{
    public void Configure(EntityTypeBuilder<PlaylistTrack> builder)
    {
        builder.HasKey(pt => new { pt.PlaylistId, pt.TrackId });
        // Alternate identity for the web API's entry-based removal. The DB default backfills
        // rows that predate the column; new rows get their id from the entity initializer.
        builder.Property(pt => pt.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(pt => pt.Id).IsUnique();
        builder.HasOne(pt => pt.Playlist).WithMany(p => p.PlaylistTracks).HasForeignKey(pt => pt.PlaylistId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(pt => pt.Track).WithMany().HasForeignKey(pt => pt.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class StarConfiguration : IEntityTypeConfiguration<Star>
{
    public void Configure(EntityTypeBuilder<Star> builder)
    {
        builder.HasKey(s => new { s.UserId, s.ItemType, s.ItemId });
        builder.HasIndex(s => new { s.UserId, s.ItemType, s.ItemId });
        builder.Property(s => s.ItemType).HasMaxLength(16);
        builder.HasOne(s => s.User).WithMany(u => u.Stars).HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class RatingConfiguration : IEntityTypeConfiguration<Rating>
{
    public void Configure(EntityTypeBuilder<Rating> builder)
    {
        // Deliberately the same shape as StarConfiguration above: composite key, a 16-char
        // discriminator, and a cascade only from User. ItemId carries no constraint because it
        // is polymorphic - see the note on the Rating entity.
        builder.HasKey(r => new { r.UserId, r.ItemType, r.ItemId });
        builder.HasIndex(r => new { r.UserId, r.ItemType, r.ItemId });
        builder.Property(r => r.ItemType).HasMaxLength(16);
        builder.HasOne(r => r.User).WithMany(u => u.Ratings).HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PlayQueueConfiguration : IEntityTypeConfiguration<PlayQueue>
{
    public void Configure(EntityTypeBuilder<PlayQueue> builder)
    {
        builder.HasKey(pq => pq.Id);
        builder.HasIndex(pq => pq.UserId).IsUnique();
        builder.HasOne(pq => pq.User).WithOne(u => u.PlayQueue).HasForeignKey<PlayQueue>(pq => pq.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(pq => pq.CurrentIndex).HasDefaultValue(0);
    }
}

public class PlayQueueTrackConfiguration : IEntityTypeConfiguration<PlayQueueTrack>
{
    public void Configure(EntityTypeBuilder<PlayQueueTrack> builder)
    {
        builder.HasKey(pqt => new { pqt.PlayQueueId, pqt.TrackId });
        builder.HasOne(pqt => pqt.PlayQueue).WithMany(pq => pq.PlayQueueTracks).HasForeignKey(pqt => pqt.PlayQueueId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(pqt => pqt.Track).WithMany().HasForeignKey(pqt => pqt.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class BookmarkConfiguration : IEntityTypeConfiguration<Bookmark>
{
    public void Configure(EntityTypeBuilder<Bookmark> builder)
    {
        builder.HasKey(b => b.Id);
        builder.HasOne(b => b.User).WithMany(u => u.Bookmarks).HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(b => b.Track).WithMany().HasForeignKey(b => b.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ScanLogConfiguration : IEntityTypeConfiguration<ScanLog>
{
    public void Configure(EntityTypeBuilder<ScanLog> builder)
    {
        builder.HasKey(sl => sl.Id);
        builder.Property(sl => sl.Status).HasMaxLength(16);
    }
}

public class StatSnapshotConfiguration : IEntityTypeConfiguration<StatSnapshot>
{
    public void Configure(EntityTypeBuilder<StatSnapshot> builder)
    {
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => s.CapturedAt);
    }
}

public class TranscodeCacheConfiguration : IEntityTypeConfiguration<TranscodeCache>
{
    public void Configure(EntityTypeBuilder<TranscodeCache> builder)
    {
        builder.HasKey(tc => tc.Id);
        builder.HasIndex(tc => new { tc.TrackId, tc.Format, tc.BitRate }).IsUnique();
        builder.Property(tc => tc.Format).IsRequired().HasMaxLength(16);
        builder.Property(tc => tc.FilePath).IsRequired().HasMaxLength(1024);
        builder.HasOne(tc => tc.Track).WithMany().HasForeignKey(tc => tc.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class InternetRadioStationConfiguration : IEntityTypeConfiguration<InternetRadioStation>
{
    public void Configure(EntityTypeBuilder<InternetRadioStation> builder)
    {
        builder.HasKey(irs => irs.Id);
        builder.Property(irs => irs.Name).IsRequired().HasMaxLength(256);
        builder.Property(irs => irs.StreamUrl).IsRequired().HasMaxLength(2048);
        builder.Property(irs => irs.HomepageUrl).HasMaxLength(2048);
    }
}


public class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.HasKey(ak => ak.Id);
        builder.Property(ak => ak.KeyHash).IsRequired().HasMaxLength(128);
        builder.Property(ak => ak.Name).HasMaxLength(256);
        builder.HasOne(ak => ak.User).WithMany(u => u.ApiKeys).HasForeignKey(ak => ak.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PlaybackDeviceConfiguration : IEntityTypeConfiguration<PlaybackDevice>
{
    public void Configure(EntityTypeBuilder<PlaybackDevice> builder)
    {
        builder.HasKey(pd => pd.Id);
        builder.Property(pd => pd.DriverId).IsRequired().HasMaxLength(32);
        // A UPnP UDN is a "uuid:" URN; mDNS instance names are bounded well below this.
        builder.Property(pd => pd.DeviceKey).IsRequired().HasMaxLength(256);
        builder.HasIndex(pd => new { pd.DriverId, pd.DeviceKey }).IsUnique();
        builder.Property(pd => pd.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(pd => pd.UserDisplayName).HasMaxLength(256);
        // Host or IP plus port, so this is generous rather than tight.
        builder.Property(pd => pd.LastAddress).HasMaxLength(256);
        builder.Property(pd => pd.TranscodeProfile).HasMaxLength(32);
        builder.Property(pd => pd.MaxVolumePercent).HasDefaultValue(100);
        builder.Property(pd => pd.LyricOffsetMs).HasDefaultValue(0);
        builder.Property(pd => pd.Enabled).HasDefaultValue(true);
    }
}

public class PlaybackSessionConfiguration : IEntityTypeConfiguration<PlaybackSession>
{
    public void Configure(EntityTypeBuilder<PlaybackSession> builder)
    {
        builder.HasKey(ps => ps.Id);
        builder.HasIndex(ps => ps.DeviceId).IsUnique();
        builder.HasOne(ps => ps.Device).WithMany().HasForeignKey(ps => ps.DeviceId).OnDelete(DeleteBehavior.Cascade);
        // No navigation back from User: a session is reached through its device, never by
        // enumerating a user's sessions.
        builder.HasOne<User>().WithMany().HasForeignKey(ps => ps.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(ps => ps.CurrentIndex).HasDefaultValue(0);
        builder.Property(ps => ps.PositionMs).HasDefaultValue(0L);
        builder.Property(ps => ps.VolumePercent).HasDefaultValue(100);
        builder.Property(ps => ps.Shuffle).HasDefaultValue(false);
    }
}

public class PlaybackSessionTrackConfiguration : IEntityTypeConfiguration<PlaybackSessionTrack>
{
    public void Configure(EntityTypeBuilder<PlaybackSessionTrack> builder)
    {
        builder.HasKey(pst => pst.Id);
        builder.HasIndex(pst => new { pst.SessionId, pst.Position });
        builder.HasOne(pst => pst.Session).WithMany(ps => ps.Tracks).HasForeignKey(pst => pst.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(pst => pst.Track).WithMany().HasForeignKey(pst => pst.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TrackAudioFeatureConfiguration : IEntityTypeConfiguration<TrackAudioFeature>
{
    public void Configure(EntityTypeBuilder<TrackAudioFeature> builder)
    {
        builder.HasKey(f => f.TrackId);
        builder.HasOne(f => f.Track).WithOne().HasForeignKey<TrackAudioFeature>(f => f.TrackId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(f => f.Raw).HasColumnType($"vector({Cosine.Audio.FeatureLayout.Dimensions})").IsRequired();
        builder.Property(f => f.Normalized).HasColumnType($"vector({Cosine.Audio.FeatureLayout.Dimensions})");

        // Drives incremental analysis: "everything below the current feature version".
        builder.HasIndex(f => f.FeatureVersion);

        // HNSW over L2, which is the distance the facet weighting is built around - squared L2
        // over the concatenated, sqrt(weight)-scaled blocks IS the weighted sum of per-facet
        // distances. Cosine would renormalize globally and silently break that.
        builder.HasIndex(f => f.Normalized)
            .HasMethod("hnsw")
            .HasOperators("vector_l2_ops");
    }
}

public class ListenPartyConfiguration : IEntityTypeConfiguration<ListenParty>
{
    public void Configure(EntityTypeBuilder<ListenParty> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(256);
        // The share-link secret. Unique so a token resolves to exactly one party; length is
        // generous for a base64url of 32 random bytes.
        builder.Property(p => p.Token).IsRequired().HasMaxLength(128);
        builder.HasIndex(p => p.Token).IsUnique();
        // The cleanup sweep filters on (live, idle since); EndedAt first so live rows are the
        // small side of the index.
        builder.HasIndex(p => new { p.EndedAt, p.LastActivityAt });
        // One live party per owner, enforced where races can't reach: two tabs clicking
        // "start" together both pass the existing-party check, and the loser's party would
        // otherwise linger live to resurrect - old guests and all - on a later create.
        builder.HasIndex(p => p.OwnerUserId).IsUnique().HasFilter("\"EndedAt\" IS NULL");
        // No navigation back from User, same as PlaybackSession: a party is reached through
        // its token or its owner query, never by enumerating a user's parties.
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ListenPartyTrackConfiguration : IEntityTypeConfiguration<ListenPartyTrack>
{
    public void Configure(EntityTypeBuilder<ListenPartyTrack> builder)
    {
        builder.HasKey(pt => pt.Id);
        builder.HasIndex(pt => new { pt.PartyId, pt.Position });
        builder.HasOne(pt => pt.Party).WithMany(p => p.Tracks).HasForeignKey(pt => pt.PartyId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(pt => pt.Track).WithMany().HasForeignKey(pt => pt.TrackId).OnDelete(DeleteBehavior.Cascade);
        // SetNull, not cascade: losing the member row should demote the entry to "added by the
        // owner", not delete music from the queue.
        builder.HasOne(pt => pt.AddedByMember).WithMany().HasForeignKey(pt => pt.AddedByMemberId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class ListenPartyMemberConfiguration : IEntityTypeConfiguration<ListenPartyMember>
{
    public void Configure(EntityTypeBuilder<ListenPartyMember> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.DisplayName).IsRequired().HasMaxLength(64);
        builder.HasIndex(m => m.PartyId);
        builder.HasOne(m => m.Party).WithMany(p => p.Members).HasForeignKey(m => m.PartyId).OnDelete(DeleteBehavior.Cascade);
        // A member may be a logged-in user, but only for display - no FK constraint games on
        // deletion beyond clearing the reference.
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}
