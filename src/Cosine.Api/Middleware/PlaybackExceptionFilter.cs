using Cosine.Infrastructure.Playback;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cosine.Api.Middleware;

/// <summary>
/// Maps playback failures onto status codes. Applied per controller rather than globally:
/// <see cref="SubsonicExceptionFilter"/> is registered globally but returns early for anything
/// outside <c>/rest</c>, and there is no <c>UseExceptionHandler</c> in the pipeline, so without
/// this a device-busy conflict would surface as a bare 500.
/// </summary>
public sealed class PlaybackExceptionFilter : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not PlaybackException ex) return;

        context.Result = ex switch
        {
            // The feature is switched off by configuration, not broken. 503 says "not right now"
            // and keeps the message, which names the setting to fix.
            PlaybackNotConfiguredException =>
                Problem(context, StatusCodes.Status503ServiceUnavailable, "Remote playback unavailable", ex.Message),

            PlaybackDeviceBusyException busy =>
                new ConflictObjectResult(new
                {
                    title = "Device in use",
                    detail = busy.Message,
                    deviceId = busy.DeviceId,
                    ownerUserId = busy.OwnerUserId,
                    ownerUsername = busy.OwnerUsername,
                }),

            PlaybackDeviceOfflineException =>
                Problem(context, StatusCodes.Status409Conflict, "Device offline", ex.Message),

            // A conflict with cosine's own configuration rather than a bad request: the caller
            // asked for something reasonable that this install has switched off.
            PlaybackDeviceDisabledException =>
                Problem(context, StatusCodes.Status409Conflict, "Device disabled", ex.Message),

            PlaybackSettingInvalidException =>
                Problem(context, StatusCodes.Status400BadRequest, "Invalid device setting", ex.Message),

            PlaybackNotOwnerException =>
                Problem(context, StatusCodes.Status403Forbidden, "Not your session", ex.Message),

            PlaybackFormatUnavailableException =>
                Problem(context, StatusCodes.Status415UnsupportedMediaType, "Format unavailable", ex.Message),

            PlaybackSessionNotFoundException or PlaybackDeviceNotFoundException or PlaybackTrackNotFoundException =>
                Problem(context, StatusCodes.Status404NotFound, "Not found", ex.Message),

            _ => Problem(context, StatusCodes.Status500InternalServerError, "Playback error", ex.Message),
        };

        context.ExceptionHandled = true;
    }

    private static ObjectResult Problem(ExceptionContext context, int status, string title, string detail) =>
        new(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = context.HttpContext.Request.Path,
        })
        { StatusCode = status };
}
