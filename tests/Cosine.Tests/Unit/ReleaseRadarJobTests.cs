using System.Reflection;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Jobs;

namespace Cosine.Tests.Unit;

public class ReleaseRadarJobTests
{
    // --- Scan-time release-type extraction (ScanJob private statics) ---

    private static string? InvokeGetTagValue(IDictionary<string, string> fields, string key)
    {
        var method = typeof(ScanJob).GetMethod("GetTagValue", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [fields, key]);
    }

    private static string? InvokeNormalize(string? raw)
    {
        var method = typeof(ScanJob).GetMethod("NormalizeReleaseType", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [raw]);
    }

    [Fact]
    public void GetTagValue_ReadsMusicBrainzAlbumType_IgnoringSceneTagCollision()
    {
        // The scene "RELEASE TYPE" tag normalizes to the same key as "RELEASETYPE";
        // keying on MUSICBRAINZALBUMTYPE sidesteps that non-deterministic collision.
        var fields = new Dictionary<string, string>
        {
            ["MUSICBRAINZ_ALBUMTYPE"] = "album",
            ["RELEASETYPE"] = "album",
            ["RELEASE TYPE"] = "Normal release",
        };

        Assert.Equal("album", InvokeGetTagValue(fields, "MUSICBRAINZALBUMTYPE"));
    }

    [Fact]
    public void GetTagValue_MissingKey_ReturnsNull()
    {
        var fields = new Dictionary<string, string> { ["GENRE"] = "Rock" };
        Assert.Null(InvokeGetTagValue(fields, "MUSICBRAINZALBUMTYPE"));
    }

    [Theory]
    [InlineData("Album; Compilation", "album")]
    [InlineData("Single", "single")]
    [InlineData("EP / Live", "ep")]
    [InlineData("  Album  ", "album")]
    public void NormalizeReleaseType_TakesPrimaryTokenLowercased(string raw, string expected)
    {
        Assert.Equal(expected, InvokeNormalize(raw));
    }

    [Fact]
    public void NormalizeReleaseType_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(InvokeNormalize(null));
        Assert.Null(InvokeNormalize("   "));
    }

    // --- Job materialization (ReleaseRadarJob.RebuildForUserAsync private static) ---

    private static async Task<int> RebuildForUserAsync(CosineDbContext db, Guid userId, int windowDays, DateTimeOffset now)
    {
        var method = typeof(ReleaseRadarJob).GetMethod("RebuildForUserAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (Task<int>)method!.Invoke(null, [db, userId, windowDays, now, CancellationToken.None])!;
        return await task;
    }

    private static User AddUser(CosineDbContext db)
    {
        var user = new User { Id = Guid.NewGuid(), Username = "radar", PasswordHash = "h", SubsonicSecret = new string('a', 64) };
        db.Users.Add(user);
        return user;
    }

    private static Album AddAlbum(CosineDbContext db, Artist artist, string name, string? releaseType, DateTimeOffset createdAt, int trackCount)
    {
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = name, SortName = name.ToLower(),
            ArtistId = artist.Id, ReleaseType = releaseType, CreatedAt = createdAt, TrackCount = trackCount
        };
        db.Albums.Add(album);
        for (var i = 0; i < trackCount; i++)
        {
            db.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", SortTitle = $"{name} {i}".ToLower(),
                AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{Guid.NewGuid()}.mp3",
                TrackNumber = i + 1, DurationMs = 180000
            });
        }
        return album;
    }

    [Fact]
    public async Task RebuildForUser_AlbumContributesOneTrack_SingleContributesWholeRelease()
    {
        using var db = TestDbContextFactory.Create();
        var now = DateTimeOffset.UtcNow;

        var user = AddUser(db);
        var affinityArtist = new Artist { Id = Guid.NewGuid(), Name = "Loved", SortName = "loved" };
        db.Artists.Add(affinityArtist);

        // The user's affinity: a starred track by the artist (from their back catalog).
        var catalog = AddAlbum(db, affinityArtist, "Catalog", "album", now.AddYears(-1), trackCount: 1);
        await db.SaveChangesAsync();
        var catalogTrackId = db.Tracks.First(t => t.AlbumId == catalog.Id).Id;
        db.Stars.Add(new Star { UserId = user.Id, ItemType = "track", ItemId = catalogTrackId });

        // New releases inside the window.
        AddAlbum(db, affinityArtist, "New Album", "album", now, trackCount: 4);
        var single = AddAlbum(db, affinityArtist, "New Single", "single", now, trackCount: 1);
        await db.SaveChangesAsync();

        var populated = await RebuildForUserAsync(db, user.Id, windowDays: 7, now);

        Assert.Equal(1, populated);
        var playlist = db.Playlists.First(p => p.OwnerId == user.Id && p.IsSystem);
        Assert.Equal(ReleaseRadarJob.PlaylistName, playlist.Name);
        // One representative from the album + the single's one track = 2.
        Assert.Equal(2, playlist.TrackCount);

        var singleTrackId = db.Tracks.First(t => t.AlbumId == single.Id).Id;
        var radarTrackIds = db.PlaylistTracks.Where(pt => pt.PlaylistId == playlist.Id).Select(pt => pt.TrackId).ToList();
        Assert.Contains(singleTrackId, radarTrackIds);
    }

    [Fact]
    public async Task RebuildForUser_ExcludesOldReleasesAndNonAffinityArtists()
    {
        using var db = TestDbContextFactory.Create();
        var now = DateTimeOffset.UtcNow;

        var user = AddUser(db);
        var loved = new Artist { Id = Guid.NewGuid(), Name = "Loved", SortName = "loved" };
        var ignored = new Artist { Id = Guid.NewGuid(), Name = "Ignored", SortName = "ignored" };
        db.Artists.AddRange(loved, ignored);

        var catalog = AddAlbum(db, loved, "Catalog", "album", now.AddYears(-1), trackCount: 1);
        await db.SaveChangesAsync();
        db.Stars.Add(new Star { UserId = user.Id, ItemType = "track", ItemId = db.Tracks.First(t => t.AlbumId == catalog.Id).Id });

        AddAlbum(db, loved, "Old Release", "album", now.AddDays(-30), trackCount: 3); // outside window
        AddAlbum(db, ignored, "Fresh but unloved", "album", now, trackCount: 3);       // not an affinity artist
        await db.SaveChangesAsync();

        var populated = await RebuildForUserAsync(db, user.Id, windowDays: 7, now);

        Assert.Equal(0, populated);
        Assert.False(db.Playlists.Any(p => p.OwnerId == user.Id && p.IsSystem));
    }

    [Fact]
    public async Task RebuildForUser_PlaylistTrackAffinity_CountsTowardArtists()
    {
        using var db = TestDbContextFactory.Create();
        var now = DateTimeOffset.UtcNow;

        var user = AddUser(db);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Loved", SortName = "loved" };
        db.Artists.Add(artist);

        // Affinity via a normal (non-system) playlist containing the artist's track.
        var catalog = AddAlbum(db, artist, "Catalog", "album", now.AddYears(-1), trackCount: 1);
        await db.SaveChangesAsync();
        var catalogTrackId = db.Tracks.First(t => t.AlbumId == catalog.Id).Id;
        var userPlaylist = new Playlist { Id = Guid.NewGuid(), Name = "Mine", OwnerId = user.Id };
        userPlaylist.PlaylistTracks.Add(new PlaylistTrack { TrackId = catalogTrackId, Position = 1 });
        db.Playlists.Add(userPlaylist);

        AddAlbum(db, artist, "New Single", "single", now, trackCount: 1);
        await db.SaveChangesAsync();

        var populated = await RebuildForUserAsync(db, user.Id, windowDays: 7, now);

        Assert.Equal(1, populated);
        var radar = db.Playlists.First(p => p.OwnerId == user.Id && p.IsSystem);
        Assert.Equal(1, radar.TrackCount);
    }
}
