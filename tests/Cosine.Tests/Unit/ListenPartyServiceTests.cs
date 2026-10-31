using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Hubs;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

public class ListenPartyServiceTests
{
    private static ListenPartyService CreateService(
        CosineDbContext db,
        out RecordingHubContext hub,
        IListenPartyStateCache? cache = null)
    {
        hub = new RecordingHubContext();
        return new ListenPartyService(
            db, hub, cache ?? new ListenPartyStateCache(),
            NullLogger<ListenPartyService>.Instance);
    }

    private static async Task<User> SeedUserAsync(CosineDbContext db, string name = "kenny")
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = name,
            PasswordHash = "x", SubsonicSecret = "x",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Track> SeedTrackAsync(CosineDbContext db, string title = "Song")
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLower(),
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{title}.mp3",
            DurationMs = 200000,
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    [Fact]
    public async Task Create_SeedsQueueInOrder_AndDropsUnknownTracks()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var a = await SeedTrackAsync(db, "A");
        var b = await SeedTrackAsync(db, "B");

        var party = await service.CreateAsync(owner.Id, "test", [a.Id, Guid.NewGuid(), b.Id]);

        var queue = await service.GetQueueAsync(party.Id);
        Assert.Equal(new[] { a.Id, b.Id }, queue.Select(i => i.TrackId));
        Assert.Equal(new[] { 0, 1 }, queue.Select(i => i.Position));
        Assert.NotEmpty(party.Token);
    }

    /// <summary>
    /// The owner asking twice wants their link back, not a second party guests can't hear.
    /// </summary>
    [Fact]
    public async Task Create_WhilePartyLive_ReturnsTheExistingParty()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);

        var first = await service.CreateAsync(owner.Id, "one", []);
        var second = await service.CreateAsync(owner.Id, "two", []);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public async Task Join_UnknownToken_ReturnsNull()
    {
        using var sp = new TestServiceProvider();
        var service = CreateService(sp.DbContext, out _);

        Assert.Null(await service.JoinAsync("nope", "guest", null));
    }

    [Fact]
    public async Task Join_EndedParty_ReturnsNull()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", []);
        await service.EndAsync(party.Id, owner.Id, isAdmin: false);

        Assert.Null(await service.JoinAsync(party.Token, "guest", null));
    }

    [Fact]
    public async Task Join_AnnouncesTheNewMemberList_WithOwnerFirst()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out var hub);
        var owner = await SeedUserAsync(db, "kenny");
        var party = await service.CreateAsync(owner.Id, "test", []);

        var member = await service.JoinAsync(party.Token, "alice", null);

        Assert.NotNull(member);
        var (target, method, args) = Assert.Single(hub.Sent);
        Assert.Equal($"group:party:{party.Id}", target);
        Assert.Equal("PartyMembers", method);
        var members = Assert.IsAssignableFrom<IReadOnlyList<ListenPartyMemberInfo>>(args[0]);
        Assert.Collection(members,
            m => { Assert.True(m.IsOwner); Assert.Equal("kenny", m.DisplayName); },
            m => { Assert.False(m.IsOwner); Assert.Equal("alice", m.DisplayName); });
    }

    [Fact]
    public async Task AddToQueue_AppendsWithAttribution_AndBroadcastsTheQueue()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out var hub);
        var owner = await SeedUserAsync(db);
        var seeded = await SeedTrackAsync(db, "Seed");
        var added = await SeedTrackAsync(db, "Added");
        var party = await service.CreateAsync(owner.Id, "test", [seeded.Id]);
        var member = await service.JoinAsync(party.Token, "alice", null);
        hub.Sent.Clear();

        var entry = await service.AddToQueueAsync(party.Id, added.Id, member!.Id);

        Assert.NotNull(entry);
        Assert.Equal(1, entry.Position);
        Assert.Equal("alice", entry.AddedBy);
        var (_, method, _) = Assert.Single(hub.Sent);
        Assert.Equal("PartyQueue", method);
    }

    [Fact]
    public async Task AddToQueue_UnknownTrack_ReturnsNull()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", []);

        Assert.Null(await service.AddToQueueAsync(party.Id, Guid.NewGuid(), null));
    }

    /// <summary>
    /// The owner's player syncs its queue wholesale; a reorder must not erase who added what.
    /// </summary>
    [Fact]
    public async Task ReplaceQueue_CarriesGuestAttributionOverByTrackId()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var a = await SeedTrackAsync(db, "A");
        var b = await SeedTrackAsync(db, "B");
        var party = await service.CreateAsync(owner.Id, "test", [a.Id]);
        var member = await service.JoinAsync(party.Token, "alice", null);
        await service.AddToQueueAsync(party.Id, b.Id, member!.Id);

        // Owner reorders: B now plays first.
        Assert.True(await service.ReplaceQueueAsync(party.Id, owner.Id, [b.Id, a.Id]));

        var queue = await service.GetQueueAsync(party.Id);
        Assert.Equal(new[] { b.Id, a.Id }, queue.Select(i => i.TrackId));
        Assert.Equal("alice", queue[0].AddedBy);
        Assert.Null(queue[1].AddedBy);
    }

    [Fact]
    public async Task ReplaceQueue_ByAnyoneButTheOwner_IsRefused()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var a = await SeedTrackAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", [a.Id]);

        Assert.False(await service.ReplaceQueueAsync(party.Id, Guid.NewGuid(), []));
        Assert.Single(await service.GetQueueAsync(party.Id));
    }

    [Fact]
    public async Task End_ByANonOwner_IsRefused_UnlessAdmin()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", []);

        Assert.False(await service.EndAsync(party.Id, Guid.NewGuid(), isAdmin: false));
        Assert.True(await service.IsLiveAsync(party.Id));

        Assert.True(await service.EndAsync(party.Id, Guid.NewGuid(), isAdmin: true));
        Assert.False(await service.IsLiveAsync(party.Id));
    }

    /// <summary>
    /// Duplicates predate the one-live-party index (two tabs racing "start"). Ending must
    /// sweep them all: a survivor resurrects - old guests included - on the next create.
    /// </summary>
    [Fact]
    public async Task End_SweepsEveryLivePartyOfTheOwner()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", []);
        var duplicate = new ListenParty
        {
            Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "dup", Token = "dup-token",
        };
        db.ListenParties.Add(duplicate);
        await db.SaveChangesAsync();

        Assert.True(await service.EndAsync(party.Id, owner.Id, isAdmin: false));

        Assert.False(await service.IsLiveAsync(party.Id));
        Assert.False(await service.IsLiveAsync(duplicate.Id));
    }

    /// <summary>
    /// A panel can hold an id that is already dead while another live party exists (the
    /// duplicate race again). "End" means "end my party", not "end this exact row".
    /// </summary>
    [Fact]
    public async Task End_WithAStaleId_StillEndsTheOwnersLiveParty()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var stale = await service.CreateAsync(owner.Id, "old", []);
        await service.EndAsync(stale.Id, owner.Id, isAdmin: false);
        var current = await service.CreateAsync(owner.Id, "new", []);

        Assert.True(await service.EndAsync(stale.Id, owner.Id, isAdmin: false));

        Assert.False(await service.IsLiveAsync(current.Id));
    }

    [Fact]
    public async Task End_BroadcastsPartyEnded_AndDropsCachedState()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var cache = new ListenPartyStateCache();
        var service = CreateService(db, out var hub, cache);
        var owner = await SeedUserAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", []);
        cache.Publish(party.Id, new ListenPartyState(null, 0, true, DateTimeOffset.UtcNow));

        Assert.True(await service.EndAsync(party.Id, owner.Id, isAdmin: false));

        Assert.Null(cache.Get(party.Id));
        var (target, method, args) = Assert.Single(hub.Sent);
        Assert.Equal($"group:party:{party.Id}", target);
        Assert.Equal("PartyEnded", method);
        Assert.Equal(party.Id, args[0]);
    }

    [Fact]
    public async Task ScopeChecks_OnlyAdmitQueuedTracksAndTheirAlbums()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out _);
        var owner = await SeedUserAsync(db);
        var queued = await SeedTrackAsync(db, "In");
        var outside = await SeedTrackAsync(db, "Out");
        var party = await service.CreateAsync(owner.Id, "test", [queued.Id]);

        Assert.True(await service.IsTrackInPartyAsync(party.Id, queued.Id));
        Assert.False(await service.IsTrackInPartyAsync(party.Id, outside.Id));
        Assert.True(await service.IsAlbumInPartyAsync(party.Id, queued.AlbumId));
        Assert.False(await service.IsAlbumInPartyAsync(party.Id, outside.AlbumId));
    }

    [Fact]
    public async Task EndExpired_EndsAnIdleParty_AndBroadcasts()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var service = CreateService(db, out var hub);
        var owner = await SeedUserAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", []);
        db.ListenParties.Single(p => p.Id == party.Id).LastActivityAt =
            DateTimeOffset.UtcNow.AddHours(-3);
        await db.SaveChangesAsync();

        var ended = await service.EndExpiredAsync(TimeSpan.FromMinutes(120));

        Assert.Equal([party.Id], ended);
        Assert.False(await service.IsLiveAsync(party.Id));
        Assert.Contains(hub.Sent, s => s.Method == "PartyEnded");
    }

    /// <summary>
    /// The database only sees a LastActivityAt write every few minutes while the owner relays
    /// state - the cache holds the live truth, and the sweep must consult it before killing a
    /// party whose group is getting a state tick a second.
    /// </summary>
    [Fact]
    public async Task EndExpired_SparesAPartyWithFreshCachedState()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var cache = new ListenPartyStateCache();
        var service = CreateService(db, out _, cache);
        var owner = await SeedUserAsync(db);
        var party = await service.CreateAsync(owner.Id, "test", []);
        db.ListenParties.Single(p => p.Id == party.Id).LastActivityAt =
            DateTimeOffset.UtcNow.AddHours(-3);
        await db.SaveChangesAsync();
        cache.Publish(party.Id, new ListenPartyState(null, 1000, true, DateTimeOffset.UtcNow));

        var ended = await service.EndExpiredAsync(TimeSpan.FromMinutes(120));

        Assert.Empty(ended);
        Assert.True(await service.IsLiveAsync(party.Id));
    }

    private sealed class RecordingHubContext : IHubContext<ListenPartyHub>
    {
        private readonly RecordingClients _clients;

        public RecordingHubContext() => _clients = new RecordingClients(this);

        public List<(string Target, string Method, object?[] Args)> Sent { get; } = [];

        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class RecordingClients(RecordingHubContext owner) : IHubClients
        {
            private IClientProxy For(string target) => new RecordingProxy(owner, target);

            public IClientProxy All => For("all");
            public IClientProxy AllExcept(IReadOnlyList<string> excluded) => For("all-except");
            public IClientProxy Client(string connectionId) => For($"connection:{connectionId}");
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => For("connections");
            public IClientProxy Group(string groupName) => For($"group:{groupName}");
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => For("groups");
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => For($"group:{groupName}");
            public IClientProxy User(string userId) => For($"user:{userId}");
            public IClientProxy Users(IReadOnlyList<string> userIds) => For("users");
        }

        private sealed class RecordingProxy(RecordingHubContext owner, string target) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                owner.Sent.Add((target, method, args));
                return Task.CompletedTask;
            }
        }
    }
}
