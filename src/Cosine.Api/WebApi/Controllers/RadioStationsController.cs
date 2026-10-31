using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// Internet radio stations. The list is global, so anyone can read it but only admins may
/// change it - same rule as the Subsonic surface.
/// </summary>
[Route("api/radio-stations")]
public class RadioStationsController(IInternetRadioService radio) : WebApiController
{
    [HttpGet]
    public async Task<ActionResult<RadioStationListResponse>> GetStations(CancellationToken ct)
    {
        var stations = await radio.GetStationsAsync(ct);
        return new RadioStationListResponse(stations.Select(WebDtoMapper.ToDto).ToList());
    }

    [HttpPost]
    [Authorize(Policy = "RequireAdmin")]
    public async Task<ActionResult<RadioStationDto>> Create(
        [FromBody] SaveRadioStationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.StreamUrl))
            return BadRequestProblem("Name and stream URL are required");

        var station = await radio.CreateStationAsync(
            request.Name, request.StreamUrl, request.HomepageUrl, ct);
        return Created($"/api/radio-stations/{station.Id}", WebDtoMapper.ToDto(station));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireAdmin")]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] SaveRadioStationRequest request, CancellationToken ct)
    {
        await radio.UpdateStationAsync(id, request.Name, request.StreamUrl, request.HomepageUrl, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await radio.DeleteStationAsync(id, ct);
        return NoContent();
    }
}
