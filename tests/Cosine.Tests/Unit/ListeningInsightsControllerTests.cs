using System.Security.Claims;
using Cosine.Api.WebApi.Controllers;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

public class ListeningInsightsControllerTests
{
    [Fact]
    public async Task Get_UsesAuthenticatedUserAndDefaultsToCurrentUtcMonth()
    {
        var service = new RecordingInsights();
        var userId = Guid.NewGuid();
        var controller = new ListeningInsightsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
                }
            }
        };
        var before = DateTimeOffset.UtcNow;
        await controller.Get(null, null, default);
        var after = DateTimeOffset.UtcNow;
        Assert.Equal(userId, service.UserId);
        Assert.True((service.Year == before.Year && service.Month == before.Month) || (service.Year == after.Year && service.Month == after.Month));
    }

    [Theory]
    [InlineData(1969, 1)]
    [InlineData(2020, 0)]
    [InlineData(2020, 13)]
    [InlineData(9999, 1)]
    public async Task Get_RejectsInvalidMonthsWithoutCallingService(int year, int month)
    {
        var service = new RecordingInsights();
        var result = await new ListeningInsightsController(service).Get(year, month, default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(Guid.Empty, service.UserId);
    }

    private sealed class RecordingInsights : IListeningInsightsService
    {
        public Guid UserId;
        public int Year;
        public int Month;
        public Task<ListeningInsights> GetAsync(Guid userId, int year, int month, CancellationToken ct = default)
        {
            UserId = userId;
            Year = year;
            Month = month;
            return Task.FromResult(new ListeningInsights(default, default, new(0, 0, 0, 0, [], [], [], [], []), 0, [], 0, 0));
        }
    }
}
