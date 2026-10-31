using Cosine.Api;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

/// <summary>
/// The first-start account used to be <c>admin</c>/<c>admin</c> with nothing prompting a
/// rotation, so an instance reachable before its owner changed it was trivially owned.
/// </summary>
public class AdminBootstrapTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public void ResolvePassword_UsesTheConfiguredPasswordWhenSet()
    {
        var (password, generated) = AdminBootstrap.ResolvePassword("hunter2");

        Assert.Equal("hunter2", password);
        Assert.False(generated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePassword_GeneratesOneWhenUnset(string? configured)
    {
        var (password, generated) = AdminBootstrap.ResolvePassword(configured);

        Assert.True(generated);
        Assert.Equal(24, password.Length);
        Assert.NotEqual(password, AdminBootstrap.ResolvePassword(configured).Password);
    }

    [Fact]
    public async Task EnsureAdmin_OnAnEmptyDatabase_CreatesAnAdminThatIsNotAdminAdmin()
    {
        using var sp = new TestServiceProvider();
        var users = sp.Get<UserService>();

        Assert.True(await AdminBootstrap.EnsureAdminAsync(users, new AuthOptions(), Logger));

        var admin = await users.GetUserByUsernameAsync(AdminBootstrap.Username);
        Assert.NotNull(admin);
        Assert.True(admin!.IsAdmin);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => users.AuthenticateAsync("admin", "admin"));
    }

    [Fact]
    public async Task EnsureAdmin_UsesTheConfiguredPassword()
    {
        using var sp = new TestServiceProvider();
        var users = sp.Get<UserService>();
        var auth = new AuthOptions { BootstrapAdminPassword = "correct horse battery staple" };

        Assert.True(await AdminBootstrap.EnsureAdminAsync(users, auth, Logger));

        var user = await users.AuthenticateAsync(AdminBootstrap.Username, "correct horse battery staple");
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task EnsureAdmin_WithUsersPresent_CreatesNothing()
    {
        using var sp = new TestServiceProvider();
        var users = sp.Get<UserService>();
        await users.CreateUserAsync("alice", "pw", isAdmin: true);

        Assert.False(await AdminBootstrap.EnsureAdminAsync(users, new AuthOptions(), Logger));

        Assert.Single(await users.GetUsersAsync());
    }
}
