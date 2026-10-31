using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class MediaLibraryScanningController : Media_Library_ScanningControllerBase
{
    private readonly IScanService _scanService;

    public MediaLibraryScanningController(IScanService scanService)
    {
        _scanService = scanService;
    }

    public override async Task<ActionResult<StartScanResponse>> StartScan(
        CancellationToken cancellationToken = default)
    {
        if (!User.HasClaim("IsAdmin", "True"))
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var scanLogId = await _scanService.StartScanAsync(cancellationToken);

        // The spec's scanStatus carries no id, but the id is what the ScanHub progress events
        // are keyed by, so it rides along in the extension data rather than being discarded.
        // Subsonic clients ignore attributes they don't know; admin UIs can correlate with it.
        var scanStatus = new Generated.ScanStatus { Scanning = true };
        scanStatus.AdditionalProperties["scanLogId"] = scanLogId;

        var response = new StartScanResponse
        {
            SubsonicResponse = new StartScanSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                ScanStatus = scanStatus
            }
        };

        return Ok(response);
    }

    public override async Task<ActionResult<GetScanStatusResponse>> GetScanStatus(
        CancellationToken cancellationToken = default)
    {
        var status = await _scanService.GetScanStatusAsync(cancellationToken);

        var response = new GetScanStatusResponse
        {
            SubsonicResponse = new GetScanStatusSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                ScanStatus = new Generated.ScanStatus
                {
                    Scanning = status.Status == "running",
                    Count = status.Status == "running" ? null : 0
                }
            }
        };

        return Ok(response);
    }

    // POST variants
    public override Task<ActionResult<StartScanResponse>> PostStartScan(
        object? body = null, CancellationToken cancellationToken = default)
        => StartScan(cancellationToken);

    public override Task<ActionResult<GetScanStatusResponse>> PostGetScanStatus(
        object? body = null, CancellationToken cancellationToken = default)
        => GetScanStatus(cancellationToken);
}
