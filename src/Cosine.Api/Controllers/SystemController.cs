using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cosine.Api.Generated;

namespace Cosine.Api.Controllers;

// Ping (and the rest) must validate credentials - clients like Feishin use it as the
// login check. Only getOpenSubsonicExtensions is anonymous, per the OpenSubsonic spec.
[Authorize]
public class SystemController : SystemControllerBase
{
    public override Task<ActionResult<SubsonicResponse>> Ping(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ActionResult<SubsonicResponse>>(Ok());
    }

    public override Task<ActionResult<SubsonicResponse>> PostPing(object? body = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ActionResult<SubsonicResponse>>(Ok());
    }

    public override Task<ActionResult<GetLicenseResponse>> GetLicense(CancellationToken cancellationToken = default)
    {
        var response = new GetLicenseResponse
        {
            SubsonicResponse = new GetLicenseSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                License = new License { Valid = true }
            }
        };
        return Task.FromResult<ActionResult<GetLicenseResponse>>(Ok(response));
    }

    public override Task<ActionResult<GetLicenseResponse>> PostGetLicense(object? body = null, CancellationToken cancellationToken = default)
    {
        return GetLicense(cancellationToken);
    }

    [AllowAnonymous]
    public override Task<ActionResult<GetOpenSubsonicExtensionsResponse>> GetOpenSubsonicExtensions(CancellationToken cancellationToken = default)
    {
        var response = new GetOpenSubsonicExtensionsResponse
        {
            SubsonicResponse = new GetOpenSubsonicExtensionsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                OpenSubsonicExtensions = new List<OpenSubsonicExtension>
                {
                    // formPost works via the middleware that folds form bodies into
                    // the query string and reroutes to the GET actions.
                    new() { Name = "formPost", Versions = new List<int> { 1 } },
                    new() { Name = "songLyrics", Versions = new List<int> { 1 } }
                }
            }
        };
        return Task.FromResult<ActionResult<GetOpenSubsonicExtensionsResponse>>(Ok(response));
    }

    [AllowAnonymous]
    public override Task<ActionResult<GetOpenSubsonicExtensionsResponse>> PostGetOpenSubsonicExtensions(object? body = null, CancellationToken cancellationToken = default)
    {
        return GetOpenSubsonicExtensions(cancellationToken);
    }

    public override Task<ActionResult<GetTokenInfoResponse>> TokenInfo(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ActionResult<GetTokenInfoResponse>>(Ok());
    }

    public override Task<ActionResult<GetTokenInfoResponse>> PostTokenInfo(object? body = null, CancellationToken cancellationToken = default)
    {
        return TokenInfo(cancellationToken);
    }
}
