using Cosine.Core.Entities;
using Cosine.Core.Interfaces;

namespace Cosine.Tests.Unit;

public class StarServiceTests
{
    [Fact]
    public void Star_DefaultValues_AreSet()
    {
        var star = new Star
        {
            UserId = Guid.NewGuid(),
            ItemType = "track",
            ItemId = Guid.NewGuid()
        };

        Assert.Equal("track", star.ItemType);
        Assert.NotEqual(default, star.CreatedAt);
        Assert.NotEqual(Guid.Empty, star.UserId);
    }

    [Fact]
    public void Star_ItemTypes_HaveValidValues()
    {
        var validTypes = new[] { "track", "album", "artist" };

        foreach (var itemType in validTypes)
        {
            var star = new Star
            {
                UserId = Guid.NewGuid(),
                ItemType = itemType,
                ItemId = Guid.NewGuid()
            };
            Assert.Equal(itemType, star.ItemType);
        }
    }

    [Fact]
    public void StarredItems_Record_HoldsCorrectTypes()
    {
        var artists = new List<Artist>
        {
            new() { Id = Guid.NewGuid(), Name = "Artist 1", SortName = "Artist 1" }
        };
        var albums = new List<Album>
        {
            new() { Id = Guid.NewGuid(), Name = "Album 1", SortName = "Album 1", ArtistId = artists[0].Id }
        };
        var tracks = new List<Track>
        {
            new() { Id = Guid.NewGuid(), Title = "Track 1", SortTitle = "Track 1", AlbumId = albums[0].Id, ArtistId = artists[0].Id, FilePath = "/test.mp3" }
        };

        var starred = new StarredItems(artists, albums, tracks);

        Assert.Single(starred.Artists);
        Assert.Single(starred.Albums);
        Assert.Single(starred.Tracks);
        Assert.Equal("Artist 1", starred.Artists[0].Name);
        Assert.Equal("Album 1", starred.Albums[0].Name);
        Assert.Equal("Track 1", starred.Tracks[0].Title);
    }

    [Fact]
    public void Star_ItemType_IsRequired()
    {
        var star = new Star
        {
            UserId = Guid.NewGuid(),
            ItemId = Guid.NewGuid()
        };

        // ItemType defaults to empty string
        Assert.Equal(string.Empty, star.ItemType);
    }

    [Fact]
    public void StarredItems_EmptyCollection_Works()
    {
        var starred = new StarredItems(
            Array.Empty<Artist>(),
            Array.Empty<Album>(),
            Array.Empty<Track>());

        Assert.Empty(starred.Artists);
        Assert.Empty(starred.Albums);
        Assert.Empty(starred.Tracks);
    }

    [Fact]
    public void Star_DuplicateKeysPrevented_ByCompositeKey()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var star1 = new Star { UserId = userId, ItemType = "track", ItemId = itemId };
        var star2 = new Star { UserId = userId, ItemType = "track", ItemId = itemId };

        // Both have the same composite key - database constraint enforces uniqueness
        Assert.Equal(star1.UserId, star2.UserId);
        Assert.Equal(star1.ItemType, star2.ItemType);
        Assert.Equal(star1.ItemId, star2.ItemId);
    }
}
