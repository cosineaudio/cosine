using Cosine.Core.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Data;

// IDataProtectionKeyContext lets ASP.NET store its Data Protection key ring in Postgres
// instead of an ephemeral container path - no key volume to manage, and no startup
// warning about keys being lost on recreate.
public class CosineDbContext : DbContext, IDataProtectionKeyContext
{
    public CosineDbContext(DbContextOptions<CosineDbContext> options) : base(options)
    {
    }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Lyric> Lyrics => Set<Lyric>();
    public DbSet<TrackArtist> TrackArtists => Set<TrackArtist>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<TrackGenre> TrackGenres => Set<TrackGenre>();
    public DbSet<AlbumGenre> AlbumGenres => Set<AlbumGenre>();
    public DbSet<ArtistGenre> ArtistGenres => Set<ArtistGenre>();
    public DbSet<PlayHistory> PlayHistory => Set<PlayHistory>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<SearchIndex> SearchIndex => Set<SearchIndex>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistTrack> PlaylistTracks => Set<PlaylistTrack>();
    public DbSet<Star> Stars => Set<Star>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<PlayQueue> PlayQueues => Set<PlayQueue>();
    public DbSet<PlayQueueTrack> PlayQueueTracks => Set<PlayQueueTrack>();
    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();
    public DbSet<ScanLog> ScanLogs => Set<ScanLog>();
    public DbSet<TranscodeCache> TranscodeCache => Set<TranscodeCache>();
    public DbSet<InternetRadioStation> InternetRadioStations => Set<InternetRadioStation>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<StatSnapshot> StatSnapshots => Set<StatSnapshot>();
    public DbSet<PlaybackDevice> PlaybackDevices => Set<PlaybackDevice>();
    public DbSet<PlaybackSession> PlaybackSessions => Set<PlaybackSession>();
    public DbSet<PlaybackSessionTrack> PlaybackSessionTracks => Set<PlaybackSessionTrack>();
    public DbSet<TrackAudioFeature> TrackAudioFeatures => Set<TrackAudioFeature>();
    public DbSet<ListenParty> ListenParties => Set<ListenParty>();
    public DbSet<ListenPartyTrack> ListenPartyTracks => Set<ListenPartyTrack>();
    public DbSet<ListenPartyMember> ListenPartyMembers => Set<ListenPartyMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // TrackAudioFeature is Postgres-only: its columns are pgvector `vector` types and one of
        // them carries an HNSW index, neither of which any other provider can express. Modelling
        // that here rather than papering over it in the test context means the InMemory provider
        // used by the unit suite simply does not have the entity - which is the truth, and which
        // makes a unit test that forgets to gate on IVectorSupport fail loudly instead of
        // silently pretending the table is there.
        var isPostgres = Database.IsNpgsql();

        if (isPostgres)
        {
            // The migration that installs the extension is itself guarded, so on a server where
            // pgvector was never packaged it no-ops and IVectorSupport reports false.
            modelBuilder.HasPostgresExtension("vector");
        }

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(CosineDbContext).Assembly,
            type => isPostgres || type != typeof(Configurations.TrackAudioFeatureConfiguration));

        if (!isPostgres)
            modelBuilder.Ignore<TrackAudioFeature>();
    }
}
