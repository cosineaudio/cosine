using System.Security.Claims;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// Base for the web client's own REST API. Everything here is cookie-authenticated plain
/// JSON with ProblemDetails errors - none of the Subsonic machinery (envelope formatter,
/// filters, always-200 errors) applies because all of it self-scopes to <c>/rest</c>.
/// </summary>
[ApiController]
[Authorize]
public abstract class WebApiController : ControllerBase
{
    protected Guid CurrentUserId
    {
        get
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            return Guid.Parse(sub!);
        }
    }

    protected string CurrentUsername => User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    protected bool IsAdmin => User.FindFirst("IsAdmin")?.Value == "True";

    protected ObjectResult ForbiddenProblem(string title)
        => StatusCode(StatusCodes.Status403Forbidden,
            new ProblemDetails { Title = title, Status = StatusCodes.Status403Forbidden });

    protected NotFoundObjectResult NotFoundProblem(string title)
        => NotFound(new ProblemDetails { Title = title, Status = StatusCodes.Status404NotFound });

    protected BadRequestObjectResult BadRequestProblem(string title)
        => BadRequest(new ProblemDetails { Title = title, Status = StatusCodes.Status400BadRequest });

    protected Task ApplyAsync(UserDataOverlay overlay, IRatingService ratings, CancellationToken ct)
        => overlay.ApplyAsync(ratings, CurrentUserId, ct);
}
