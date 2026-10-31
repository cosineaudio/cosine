using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class PlaylistServiceDbTests
{
    private async Task<Track> SeedTrackAsync(Cosine.Infrastructure.Data.CosineDbContext db, string title = "Test Track", long durationMs = 200000)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLower(),
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{title}.mp3",
            DurationMs = durationMs
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    private async Task<User> SeedUserAsync(Cosine.Infrastructure.Data.CosineDbContext db, string username = "testuser")
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = username,
            PasswordHash = "hash", SubsonicSecret = new string('a', 64)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task CreatePlaylist_SetsAllProperties()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();

        var userId = Guid.NewGuid();
        var playlist = await service.CreatePlaylistAsync("My Playlist", userId);

        Assert.NotEqual(Guid.Empty, playlist.Id);
        Assert.Equal("My Playlist", playlist.Name);
        Assert.Equal(userId, playlist.OwnerId);
        Assert.Equal(0, playlist.TrackCount);
        Assert.Equal(0L, playlist.DurationMs);
        Assert.False(playlist.IsPublic);
    }

    [Fact]
    public async Task GetPlaylists_ReturnsUserAndPublicPlaylists()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user1 = await SeedUserAsync(db, "user1");
        var user2 = await SeedUserAsync(db, "user2");

        await service.CreatePlaylistAsync("User1 Private", user1.Id);
        var pub = await service.CreatePlaylistAsync("User1 Public", user1.Id);
        await service.UpdatePlaylistAsync(pub.Id, isPublic: true);
        await service.CreatePlaylistAsync("User2 Private", user2.Id);

        var user1Lists = await service.GetPlaylistsAsync(user1.Id);
        var user2Lists = await service.GetPlaylistsAsync(user2.Id);

        Assert.Equal(2, user1Lists.Count);
        Assert.Equal(2, user2Lists.Count);
    }

    [Fact]
    public async Task GetPlaylist_IncludesTracks()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var track = await SeedTrackAsync(db);

        var playlist = await service.CreatePlaylistAsync("Test", user.Id);
        await service.AddTracksAsync(playlist.Id, [track.Id]);

        var fetched = await service.GetPlaylistAsync(playlist.Id);

        Assert.NotNull(fetched);
        Assert.Single(fetched!.PlaylistTracks);
        Assert.Equal(track.Id, fetched.PlaylistTracks.First().TrackId);
    }

    [Fact]
    public async Task GetPlaylist_NonExistent_ReturnsNull()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();

        var result = await service.GetPlaylistAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdatePlaylist_ChangesFields()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var playlist = await service.CreatePlaylistAsync("Original", user.Id);

        await service.UpdatePlaylistAsync(playlist.Id, name: "Updated", comment: "Comment", isPublic: true);

        var updated = await service.GetPlaylistAsync(playlist.Id);
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated!.Name);
        Assert.Equal("Comment", updated.Comment);
        Assert.True(updated.IsPublic);
    }

    [Fact]
    public async Task UpdatePlaylist_NonExistent_Throws()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdatePlaylistAsync(Guid.NewGuid(), name: "Test"));
    }

    [Fact]
    public async Task DeletePlaylist_RemovesRecord()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var playlist = await service.CreatePlaylistAsync("To Delete", user.Id);

        await service.DeletePlaylistAsync(playlist.Id);

        var result = await service.GetPlaylistAsync(playlist.Id);
        Assert.Null(result);
    }

    [Fact]
    public async Task DeletePlaylist_NonExistent_DoesNotThrow()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();

        await service.DeletePlaylistAsync(Guid.NewGuid());

        Assert.True(true);
    }

    [Fact]
    public async Task AddTracks_CalculatesDenormalizedCounters()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var track1 = await SeedTrackAsync(db, "Track 1", 180000);
        var track2 = await SeedTrackAsync(db, "Track 2", 240000);

        var playlist = await service.CreatePlaylistAsync("Counter Test", user.Id);
        await service.AddTracksAsync(playlist.Id, [track1.Id, track2.Id]);

        var updated = await service.GetPlaylistAsync(playlist.Id);
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.TrackCount);
        Assert.Equal(420000L, updated.DurationMs);
    }

    [Fact]
    public async Task AddTracks_SkipsDuplicates()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var track = await SeedTrackAsync(db);

        var playlist = await service.CreatePlaylistAsync("Dup Test", user.Id);
        await service.AddTracksAsync(playlist.Id, [track.Id]);
        await service.AddTracksAsync(playlist.Id, [track.Id]);

        var updated = await service.GetPlaylistAsync(playlist.Id);
        Assert.Equal(1, updated!.TrackCount);
    }

    [Fact]
    public async Task AddTracks_NonExistentPlaylist_Throws()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddTracksAsync(Guid.NewGuid(), [Guid.NewGuid()]));
    }

    [Fact]
    public async Task RemoveTrack_UpdatesDenormalizedCounters()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var track1 = await SeedTrackAsync(db, "T1", 100000);
        var track2 = await SeedTrackAsync(db, "T2", 200000);

        var playlist = await service.CreatePlaylistAsync("Remove Test", user.Id);
        await service.AddTracksAsync(playlist.Id, [track1.Id, track2.Id]);
        await service.RemoveTrackAsync(playlist.Id, track1.Id);

        var updated = await service.GetPlaylistAsync(playlist.Id);
        Assert.Equal(1, updated!.TrackCount);
        Assert.Equal(200000L, updated.DurationMs);
    }

    [Fact]
    public async Task RemoveTrack_NonExistent_DoesNotThrow()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var playlist = await service.CreatePlaylistAsync("Test", user.Id);
        await service.RemoveTrackAsync(playlist.Id, Guid.NewGuid());

        Assert.True(true);
    }

    [Fact]
    public async Task Reorder_SetsCorrectPositions()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var track1 = await SeedTrackAsync(db, "T1");
        var track2 = await SeedTrackAsync(db, "T2");
        var track3 = await SeedTrackAsync(db, "T3");

        var playlist = await service.CreatePlaylistAsync("Reorder Test", user.Id);
        await service.AddTracksAsync(playlist.Id, [track1.Id, track2.Id, track3.Id]);

        await service.ReorderAsync(playlist.Id, [track3.Id, track1.Id, track2.Id]);

        var updated = await service.GetPlaylistAsync(playlist.Id);
        var tracks = updated!.PlaylistTracks.OrderBy(pt => pt.Position).ToList();
        Assert.Equal(track3.Id, tracks[0].TrackId);
        Assert.Equal(track1.Id, tracks[1].TrackId);
        Assert.Equal(track2.Id, tracks[2].TrackId);
    }

    [Fact]
    public async Task Reorder_EmptyPlaylist_DoesNotThrow()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlaylistService>();
        var db = sp.DbContext;

        var user = await SeedUserAsync(db);
        var playlist = await service.CreatePlaylistAsync("Empty", user.Id);

        await service.ReorderAsync(playlist.Id, []);

        Assert.True(true);
    }
}
