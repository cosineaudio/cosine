using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

[Route("api/me/insights")]
public sealed class ListeningInsightsController(IListeningInsightsService insights) : WebApiController
{
    [HttpGet]
    public async Task<ActionResult<ListeningInsights>> Get([FromQuery] int? year, [FromQuery] int? month, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var y = year ?? now.Year;
        var m = month ?? now.Month;
        if (y < 1970 || y > now.Year || m is < 1 or > 12 || (y == now.Year && m > now.Month))
            return BadRequestProblem("Choose a valid month up to the current month");
        return Ok(await insights.GetAsync(CurrentUserId, y, m, ct));
    }
}
