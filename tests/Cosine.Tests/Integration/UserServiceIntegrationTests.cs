using Cosine.Infrastructure.Services;
using Cosine.Core.Entities;

namespace Cosine.Tests.Integration;

public class UserServiceIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public UserServiceIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateUser_And_Authenticate_With_Real_Postgres()
    {
        var service = new UserService(_fixture.DbContext);

        var user = await service.CreateUserAsync("inttest_user", "securepass123", "inttest@example.com");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("inttest_user", user.Username);
        Assert.Equal(64, user.SubsonicSecret.Length);

        var authenticated = await service.AuthenticateAsync("inttest_user", "securepass123");
        Assert.NotNull(authenticated);
        Assert.Equal(user.Id, authenticated.Id);
        Assert.NotNull(authenticated.LastLoginAt);
    }

    [Fact]
    public async Task SubsonicAuth_Works_With_Real_Postgres()
    {
        var service = new UserService(_fixture.DbContext);

        var user = await service.CreateUserAsync("sub_int_user", "password");
        var salt = "randomsalt";
        var expectedToken = ComputeMd5(user.SubsonicSecret + salt);

        var result = await service.AuthenticateSubsonicAsync("sub_int_user", expectedToken, salt);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task GetUsers_Returns_Ordered_List()
    {
        var service = new UserService(_fixture.DbContext);

        await service.CreateUserAsync("zebra", "pw");
        await service.CreateUserAsync("apple", "pw");
        await service.CreateUserAsync("mango", "pw");

        var users = await service.GetUsersAsync();

        Assert.True(users.Count >= 3);
        var names = users.Select(u => u.Username).ToList();
        Assert.True(names.IndexOf("apple") < names.IndexOf("mango"));
        Assert.True(names.IndexOf("mango") < names.IndexOf("zebra"));
    }

    [Fact]
    public async Task UpdateUser_Persists_Changes()
    {
        var service = new UserService(_fixture.DbContext);

        var user = await service.CreateUserAsync("update_int_user", "pw");
        await service.UpdateUserAsync(user.Id, email: "updated@example.com", isAdmin: true);

        var updated = await service.GetUserAsync(user.Id);
        Assert.NotNull(updated);
        Assert.Equal("updated@example.com", updated!.Email);
        Assert.True(updated.IsAdmin);
    }

    [Fact]
    public async Task DeleteUser_Removes_From_Database()
    {
        var service = new UserService(_fixture.DbContext);

        var user = await service.CreateUserAsync("delete_int_user", "pw");
        await service.DeleteUserAsync(user.Id);

        var result = await service.GetUserAsync(user.Id);
        Assert.Null(result);
    }

    private static string ComputeMd5(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
