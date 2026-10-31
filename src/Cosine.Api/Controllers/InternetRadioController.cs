using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class InternetRadioController : Internet_RadioControllerBase
{
    private readonly IInternetRadioService _radioService;

    public InternetRadioController(IInternetRadioService radioService)
    {
        _radioService = radioService;
    }

    public override async Task<ActionResult<SubsonicResponse>> CreateInternetRadioStation(
        string streamUrl, string name, string? homepageUrl = null,
        CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        await _radioService.CreateStationAsync(name, streamUrl, homepageUrl, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostCreateInternetRadioStation(
        Body4 body, CancellationToken cancellationToken = default)
    {
        return CreateInternetRadioStation(body.StreamUrl, body.Name, body.HomepageUrl, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> DeleteInternetRadioStation(
        string id, CancellationToken cancellationToken = default)
    {
        RequireAdmin();

        if (!Guid.TryParse(id, out var stationId))
            throw new SubsonicException(SubsonicErrorCode._0, "Invalid station ID");

        await _radioService.DeleteStationAsync(stationId, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostDeleteInternetRadioStation(
        Body9 body, CancellationToken cancellationToken = default)
    {
        return DeleteInternetRadioStation(body.Id, cancellationToken);
    }

    public override async Task<ActionResult<GetInternetRadioStationsResponse>> GetInternetRadioStations(
        CancellationToken cancellationToken = default)
    {
        var stations = await _radioService.GetStationsAsync(cancellationToken);

        return Ok(new GetInternetRadioStationsResponse
        {
            SubsonicResponse = new GetInternetRadioStationsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                InternetRadioStations = new Generated.InternetRadioStations
                {
                    InternetRadioStation = stations.Select(MapToStation).ToList()
                }
            }
        });
    }

    public override Task<ActionResult<GetInternetRadioStationsResponse>> PostGetInternetRadioStations(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetInternetRadioStations(cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> UpdateInternetRadioStation(
        string id, string streamUrl, string name, string? homepageUrl = null,
        CancellationToken cancellationToken = default)
    {
        RequireAdmin();

        if (!Guid.TryParse(id, out var stationId))
            throw new SubsonicException(SubsonicErrorCode._0, "Invalid station ID");

        await _radioService.UpdateStationAsync(stationId, name, streamUrl, homepageUrl, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostUpdateInternetRadioStation(
        Body60 body, CancellationToken cancellationToken = default)
    {
        return UpdateInternetRadioStation(body.Id, body.StreamUrl, body.Name, body.HomepageUrl, cancellationToken);
    }

    /// <summary>
    /// Stations are a single global list, so per the Subsonic spec only admins may
    /// create, update or delete them - otherwise any user can repoint every station.
    /// </summary>
    private void RequireAdmin()
    {
        if (User.FindFirst("IsAdmin")?.Value != "True")
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");
    }

    private static Generated.InternetRadioStation MapToStation(Core.Entities.InternetRadioStation station)
    {
        return new Generated.InternetRadioStation
        {
            Id = station.Id.ToString(),
            Name = station.Name,
            StreamUrl = station.StreamUrl,
            HomePageUrl = station.HomepageUrl
        };
    }
}
