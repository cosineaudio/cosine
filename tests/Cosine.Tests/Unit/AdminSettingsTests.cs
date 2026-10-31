using Cosine.Api.Controllers;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

/// <summary>
/// #55: <c>PUT /api/admin/settings/{key}</c> wrote whatever key it was handed. Only the keys in
/// <see cref="SettingsService.Defaults"/> are ever read back or rendered, so anything else was a
/// row nothing consumes - and a key over the 128-char column produced a 500 rather than a 400.
/// </summary>
public class AdminSettingsTests
{
    // Only the settings service and the logger are exercised by UpdateSetting; the scheduler is
    // reached for "scan.schedule" alone, which the rejection paths never get to.
    private static AdminController CreateController(TestServiceProvider sp)
        => new(null!, sp.Get<SettingsService>(), null!, null!, null!,
            NullLogger<AdminController>.Instance)
        {
            // The accepted path writes an audit line naming the acting admin.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Theory]
    [InlineData("not.a.real.setting")]
    [InlineData("scan.schedule.")]
    [InlineData("")]
    public async Task UpdateSetting_UnknownKey_IsRejectedAndNotStored(string key)
    {
        using var sp = new TestServiceProvider();
        var controller = CreateController(sp);

        var result = await controller.UpdateSetting(key, new UpdateSettingRequest("x"), null!, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await sp.Get<SettingsService>().GetAsync(key));
    }

    [Fact]
    public async Task UpdateSetting_OverlongKey_IsA400NotA500()
    {
        using var sp = new TestServiceProvider();
        var controller = CreateController(sp);

        var result = await controller.UpdateSetting(
            new string('k', 200), new UpdateSettingRequest("x"), null!, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateSetting_KnownKey_IsStored()
    {
        using var sp = new TestServiceProvider();
        var controller = CreateController(sp);

        var result = await controller.UpdateSetting(
            "scan.enabled", new UpdateSettingRequest("true"), null!, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("true", await sp.Get<SettingsService>().GetAsync("scan.enabled"));
    }

    [Fact]
    public async Task UpdateSetting_InvalidCron_IsStillRejected()
    {
        using var sp = new TestServiceProvider();
        var controller = CreateController(sp);

        var result = await controller.UpdateSetting(
            "scan.schedule", new UpdateSettingRequest("not a cron"), null!, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await sp.Get<SettingsService>().GetAsync("scan.schedule"));
    }
}
