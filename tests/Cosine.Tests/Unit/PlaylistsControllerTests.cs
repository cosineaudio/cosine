using System.Security.Claims;
using Cosine.Api.Controllers;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UserEntity = Cosine.Core.Entities.User;

namespace Cosine.Tests.Unit;

/// <summary>
/// getPlaylists?username=&lt;self&gt;: several clients pass their own username. The check
/// compared it against a "username" claim that is never issued - the ticket carries
/// ClaimTypes.Name - so the null comparison always took the admin-only branch and every
/// non-admin client got error 50 for asking about itself.
/// </summary>
public class PlaylistsControllerTests
{
    private static PlaylistsController CreateController(TestServiceProvider sp, UserEntity user)
    {
        var controller = new PlaylistsController(
            sp.Get<PlaylistService>(), sp.Get<UserService>(), new FakeRatingService());
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("IsAdmin", user.IsAdmin.ToString())
        ], "Test");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static List<Playlist> PlaylistsOf(ActionResult<GetPlaylistsResponse> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<GetPlaylistsResponse>(ok.Value);
        return Assert.IsType<GetPlaylistsSuccessResponse>(response.SubsonicResponse).Playlists!.Playlist!.ToList();
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("Alice")] // authentication is case-insensitive, so this check must be too
    public async Task GetPlaylists_NonAdminAskingForOwnPlaylists_IsAllowed(string requested)
    {
        using var sp = new TestServiceProvider();
        var users = sp.Get<UserService>();
        var playlists = sp.Get<PlaylistService>();

        var alice = await users.CreateUserAsync("alice", "pw");
        await playlists.CreatePlaylistAsync("Roadtrip", alice.Id);

        var controller = CreateController(sp, alice);

        var result = await controller.GetPlaylists(requested);

        Assert.Equal("Roadtrip", Assert.Single(PlaylistsOf(result)).Name);
    }

    [Fact]
    public async Task GetPlaylists_NonAdminAskingForSomeoneElse_IsRejected()
    {
        using var sp = new TestServiceProvider();
        var users = sp.Get<UserService>();

        var alice = await users.CreateUserAsync("alice", "pw");
        await users.CreateUserAsync("bob", "pw");

        var controller = CreateController(sp, alice);

        var ex = await Assert.ThrowsAsync<SubsonicException>(() => controller.GetPlaylists("bob"));
        Assert.Equal(SubsonicErrorCode._50, ex.ErrorCode);
    }

    [Fact]
    public async Task GetPlaylists_AdminAskingForAnotherUser_ReturnsThatUsersPlaylists()
    {
        using var sp = new TestServiceProvider();
        var users = sp.Get<UserService>();
        var playlists = sp.Get<PlaylistService>();

        var admin = await users.CreateUserAsync("root", "pw", isAdmin: true);
        var bob = await users.CreateUserAsync("bob", "pw");
        await playlists.CreatePlaylistAsync("Bob's mix", bob.Id);

        var controller = CreateController(sp, admin);

        var result = await controller.GetPlaylists("bob");

        Assert.Equal("Bob's mix", Assert.Single(PlaylistsOf(result)).Name);
    }
}
