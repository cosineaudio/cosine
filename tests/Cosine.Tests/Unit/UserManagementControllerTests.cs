using System.Security.Claims;
using Cosine.Api.Controllers;
using Cosine.Api.Generated;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// `updateUser` used to strip the admin flag off any user it touched, because the generated
/// binding filled `adminRole` in with `false` whenever the request omitted it. The query path
/// is guarded by presence detection; the JSON body path could not detect presence at all until
/// the role booleans on that body were made nullable in the spec.
///
/// Every case here uses a *non-last* admin on purpose: `UserService` refuses to demote the last
/// remaining admin, and that guard masks the bug entirely.
/// </summary>
public class UserManagementControllerTests
{
    private static (UserManagementController Controller, UserService Service, CosineDbContext Db) Create()
    {
        var db = TestDbContextFactory.Create();
        var service = new UserService(db);
        var controller = new UserManagementController(service);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "caller"),
                new Claim("IsAdmin", "True")
            ],
            "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return (controller, service, db);
    }

    [Fact]
    public async Task PostUpdateUser_BodyWithoutAdminRole_LeavesTheAdminFlagSet()
    {
        var (controller, service, db) = Create();
        using var _ = db;

        await service.CreateUserAsync("keeper", "pw", isAdmin: true);
        // A second admin, so the last-admin guard cannot be what saves the flag.
        await service.CreateUserAsync("other", "pw", isAdmin: true);

        await controller.PostUpdateUser(new Body62
        {
            Username = "keeper",
            Email = "keeper@example.com"
        });

        var updated = await service.GetUserByUsernameAsync("keeper");
        Assert.True(updated!.IsAdmin);
        Assert.Equal("keeper@example.com", updated.Email);
    }

    [Fact]
    public async Task PostUpdateUser_BodyWithAdminRoleFalse_DemotesTheUser()
    {
        var (controller, service, db) = Create();
        using var _ = db;

        await service.CreateUserAsync("keeper", "pw", isAdmin: true);
        await service.CreateUserAsync("other", "pw", isAdmin: true);

        await controller.PostUpdateUser(new Body62
        {
            Username = "keeper",
            AdminRole = false
        });

        Assert.False((await service.GetUserByUsernameAsync("keeper"))!.IsAdmin);
    }

    [Fact]
    public async Task PostUpdateUser_BodyWithAdminRoleTrue_PromotesTheUser()
    {
        var (controller, service, db) = Create();
        using var _ = db;

        await service.CreateUserAsync("plain", "pw");
        await service.CreateUserAsync("other", "pw", isAdmin: true);

        await controller.PostUpdateUser(new Body62
        {
            Username = "plain",
            AdminRole = true
        });

        Assert.True((await service.GetUserByUsernameAsync("plain"))!.IsAdmin);
    }

    // The query path's guard is `ParameterSupplied`, which reads the request rather than the
    // bound value - so it has to be exercised through a request that actually lacks the key.
    [Fact]
    public async Task UpdateUser_QueryWithoutAdminRole_LeavesTheAdminFlagSet()
    {
        var (controller, service, db) = Create();
        using var _ = db;

        await service.CreateUserAsync("keeper", "pw", isAdmin: true);
        await service.CreateUserAsync("other", "pw", isAdmin: true);

        controller.HttpContext.Request.QueryString =
            new QueryString("?username=keeper&email=keeper@example.com");

        await controller.UpdateUser("keeper", password: string.Empty, email: "keeper@example.com");

        Assert.True((await service.GetUserByUsernameAsync("keeper"))!.IsAdmin);
    }

    [Fact]
    public async Task UpdateUser_QueryWithAdminRoleFalse_DemotesTheUser()
    {
        var (controller, service, db) = Create();
        using var _ = db;

        await service.CreateUserAsync("keeper", "pw", isAdmin: true);
        await service.CreateUserAsync("other", "pw", isAdmin: true);

        controller.HttpContext.Request.QueryString =
            new QueryString("?username=keeper&adminRole=false");

        await controller.UpdateUser("keeper", password: string.Empty, adminRole: false);

        Assert.False((await service.GetUserByUsernameAsync("keeper"))!.IsAdmin);
    }
}
