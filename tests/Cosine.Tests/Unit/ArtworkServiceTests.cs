using Cosine.Core.Entities;

namespace Cosine.Tests.Unit;

public class ArtworkServiceTests
{
    [Fact]
    public void Album_CoverBlob_StoresBinaryImageData()
    {
        var album = new Album
        {
            Id = Guid.NewGuid(),
            Name = "Test Album",
            SortName = "Test Album",
            ArtistId = Guid.NewGuid(),
            CoverBlob = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 } // JPEG header
        };

        Assert.NotNull(album.CoverBlob);
        Assert.NotEmpty(album.CoverBlob);
        Assert.Equal(4, album.CoverBlob.Length);
    }

    [Fact]
    public void Album_CoverBlob_CanBeNull()
    {
        var album = new Album
        {
            Id = Guid.NewGuid(),
            Name = "No Cover Album",
            SortName = "No Cover Album",
            ArtistId = Guid.NewGuid()
        };

        Assert.Null(album.CoverBlob);
    }

    [Fact]
    public void Artist_ImageBlob_StoresBinaryImageData()
    {
        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            Name = "Test Artist",
            SortName = "Test Artist",
            ImageBlob = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }
        };

        Assert.NotNull(artist.ImageBlob);
        Assert.NotEmpty(artist.ImageBlob);
    }

    [Fact]
    public void Artist_ImageBlob_CanBeNull()
    {
        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            Name = "No Image Artist",
            SortName = "No Image Artist"
        };

        Assert.Null(artist.ImageBlob);
    }

    [Fact]
    public void Album_CoverBlob_EmptyArray_IsNotNullButEmpty()
    {
        var album = new Album
        {
            Id = Guid.NewGuid(),
            Name = "Empty Cover",
            SortName = "Empty Cover",
            ArtistId = Guid.NewGuid(),
            CoverBlob = Array.Empty<byte>()
        };

        Assert.NotNull(album.CoverBlob);
        Assert.Empty(album.CoverBlob);
    }

    [Fact]
    public void Artist_ImageBlob_EmptyArray_IsNotNullButEmpty()
    {
        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            Name = "Empty Image",
            SortName = "Empty Image",
            ImageBlob = Array.Empty<byte>()
        };

        Assert.NotNull(artist.ImageBlob);
        Assert.Empty(artist.ImageBlob);
    }

    [Fact]
    public void Album_CoverBlob_LargeImageData_StoredCorrectly()
    {
        var largeBlob = new byte[1024];
        new Random(42).NextBytes(largeBlob);

        var album = new Album
        {
            Id = Guid.NewGuid(),
            Name = "Large Cover",
            SortName = "Large Cover",
            ArtistId = Guid.NewGuid(),
            CoverBlob = largeBlob
        };

        Assert.Equal(1024, album.CoverBlob.Length);
        Assert.Equal(largeBlob[0], album.CoverBlob[0]);
        Assert.Equal(largeBlob[1023], album.CoverBlob[1023]);
    }
}
