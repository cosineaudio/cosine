using System.Security.Claims;
using Cosine.Api.Controllers;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// The A–Z artist index is a browse surface, not a census. Featured/guest credits mint an
/// <see cref="Artist"/> row that owns no album (see <c>TrackArtist</c>), and left in, those
/// rows dominate the index with names that have no image and nothing to browse into. They
/// stay reachable by id, by name and through search - they just aren't listed.
/// </summary>
public class ArtistIndexTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static Artist AddArtist(
        Cosine.Infrastructure.Data.CosineDbContext db, string name, bool withAlbum, int starCount = 0)
    {
        var artist = new Artist
        {
            Id = Guid.NewGuid(), Name = name, SortName = name.ToLowerInvariant(), StarCount = starCount
        };
        db.Artists.Add(artist);

        if (withAlbum)
            db.Albums.Add(new Album
            {
                Id = Guid.NewGuid(), Name = $"{name} Album", SortName = $"{name.ToLowerInvariant()} album",
                ArtistId = artist.Id
            });

        return artist;
    }

    private static BrowsingController Create(TestServiceProvider sp)
    {
        var controller = new BrowsingController(
            sp.Get<MediaLibraryService>(), sp.Get<SimilarityService>(), new FakeRatingService());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

    [Fact]
    public async Task GetArtists_OmitsArtistsWithNoAlbumsOfTheirOwn()
    {
        using var sp = new TestServiceProvider();
        AddArtist(sp.DbContext, "Headliner", withAlbum: true);
        AddArtist(sp.DbContext, "Guest", withAlbum: false);
        await sp.DbContext.SaveChangesAsync();

        var result = await Create(sp).GetArtists();
        var names = ((result.Result as OkObjectResult)!.Value as Cosine.Api.Generated.GetArtistsResponse)!
            .SubsonicResponse!.Artists!.Index!
            .SelectMany(i => i.Artist ?? [])
            .Select(a => a.Name)
            .ToList();

        Assert.Equal(["Headliner"], names);
    }

    [Fact]
    public async Task GetArtists_KeepsAnAlbumlessArtistThatIsStarred()
    {
        using var sp = new TestServiceProvider();
        AddArtist(sp.DbContext, "Guest", withAlbum: false, starCount: 1);
        await sp.DbContext.SaveChangesAsync();

        var result = await Create(sp).GetArtists();
        var names = ((result.Result as OkObjectResult)!.Value as Cosine.Api.Generated.GetArtistsResponse)!
            .SubsonicResponse!.Artists!.Index!
            .SelectMany(i => i.Artist ?? [])
            .Select(a => a.Name)
            .ToList();

        Assert.Equal(["Guest"], names);
    }

    [Fact]
    public async Task GetIndexes_FiltersTheSameWayAsGetArtists()
    {
        using var sp = new TestServiceProvider();
        AddArtist(sp.DbContext, "Headliner", withAlbum: true);
        AddArtist(sp.DbContext, "Guest", withAlbum: false);
        await sp.DbContext.SaveChangesAsync();

        var result = await Create(sp).GetIndexes();
        var names = ((result.Result as OkObjectResult)!.Value as Cosine.Api.Generated.GetIndexesResponse)!
            .SubsonicResponse!.Indexes!.Index!
            .SelectMany(i => i.Artist ?? [])
            .Select(a => a.Name)
            .ToList();

        Assert.Equal(["Headliner"], names);
    }

    [Fact]
    public async Task GetArtist_StillResolvesAnArtistTheIndexOmits()
    {
        using var sp = new TestServiceProvider();
        var guest = AddArtist(sp.DbContext, "Guest", withAlbum: false);
        await sp.DbContext.SaveChangesAsync();

        var result = await Create(sp).GetArtist(guest.Id.ToString());
        var artist = ((result.Result as OkObjectResult)!.Value as Cosine.Api.Generated.GetArtistResponse)!
            .SubsonicResponse!.Artist!;

        Assert.Equal("Guest", artist.Name);
    }
}
