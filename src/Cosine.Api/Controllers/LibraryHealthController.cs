using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[ApiController]
[Authorize(Policy = "RequireAdmin")]
[Route("api/admin/library-health")]
public sealed class LibraryHealthController(ILibraryHealthService health) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LibraryHealthReport>> Get(CancellationToken ct)
        => Ok(await health.GetReportAsync(ct));
}
