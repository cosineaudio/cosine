using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// Playlist CRUD with proper verbs and entry-id-based removal. Access rules mirror the
/// Subsonic surface: reading needs public-or-owner-or-admin, editing needs the owner, and
/// system playlists (Release Radar) are read-only for everyone.
/// </summary>
[Route("api/playlists")]
public class PlaylistsController(
    IPlaylistService playlists,
    IRatingService ratings) : WebApiController
{
    // Route name rather than action reference for CreatedAtRoute: a Subsonic controller with
    // the same class name exists, and action-name resolution across the two is ambiguous.
    private const string GetPlaylistRouteName = "WebApiGetPlaylist";

    [HttpGet]
    public async Task<ActionResult<PlaylistListResponse>> GetPlaylists(CancellationToken ct)
    {
        var lists = await playlists.GetPlaylistsAsync(CurrentUserId, ct);
        return new PlaylistListResponse(lists.Select(WebDtoMapper.ToSummaryDto).ToList());
    }

    [HttpGet("{id:guid}", Name = GetPlaylistRouteName)]
    public async Task<ActionResult<PlaylistDetailDto>> GetPlaylist(Guid id, CancellationToken ct)
    {
        var playlist = await playlists.GetPlaylistAsync(id, ct);
        if (playlist is null)
            return NotFoundProblem("Playlist not found");
        if (!playlist.IsPublic && playlist.OwnerId != CurrentUserId && !IsAdmin)
            return ForbiddenProblem("Not authorized");

        var dto = WebDtoMapper.ToDetailDto(playlist);
        await ApplyAsync(
            new UserDataOverlay().AddRange(dto.Entries.Select(e => e.Track)), ratings, ct);
        return dto;
    }

    [HttpPost]
    public async Task<ActionResult<PlaylistDetailDto>> Create(
        [FromBody] CreatePlaylistRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequestProblem("Playlist name is required");

        var created = await playlists.CreatePlaylistAsync(request.Name, CurrentUserId, ct);
        if (request.TrackIds is { Count: > 0 })
            await playlists.AddTracksAsync(created.Id, request.TrackIds.ToArray(), ct);

        var playlist = await playlists.GetPlaylistAsync(created.Id, ct);
        return CreatedAtRoute(
            GetPlaylistRouteName, new { id = created.Id }, WebDtoMapper.ToDetailDto(playlist!));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpdatePlaylistRequest request, CancellationToken ct)
    {
        if (await AuthorizeEditAsync(id, ct) is { } error)
            return error;

        await playlists.UpdatePlaylistAsync(id, request.Name, request.Comment, request.IsPublic, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        // Admins may delete any (non-system) playlist, matching the Subsonic surface.
        if (await AuthorizeEditAsync(id, ct, allowAdmin: true) is { } error)
            return error;

        await playlists.DeletePlaylistAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/tracks")]
    public async Task<IActionResult> AddTracks(
        Guid id, [FromBody] AddPlaylistTracksRequest request, CancellationToken ct)
    {
        if (await AuthorizeEditAsync(id, ct) is { } error)
            return error;

        if (request.TrackIds is { Count: > 0 })
            await playlists.AddTracksAsync(id, request.TrackIds.ToArray(), ct);
        return NoContent();
    }

    /// <summary>Removes one entry by its own id - not a position index, not a track id.</summary>
    [HttpDelete("{id:guid}/tracks/{entryId:guid}")]
    public async Task<IActionResult> RemoveEntry(Guid id, Guid entryId, CancellationToken ct)
    {
        if (await AuthorizeEditAsync(id, ct) is { } error)
            return error;

        await playlists.RemoveEntryAsync(id, entryId, ct);
        return NoContent();
    }

    /// <summary>Clears every entry, leaving the empty playlist in place.</summary>
    [HttpDelete("{id:guid}/tracks")]
    public async Task<IActionResult> ClearTracks(Guid id, CancellationToken ct)
    {
        if (await AuthorizeEditAsync(id, ct) is { } error)
            return error;

        await playlists.ClearTracksAsync(id, ct);
        return NoContent();
    }

    /// <summary>Shared edit gate: 404 unknown, 403 for non-owners and for system playlists.</summary>
    private async Task<IActionResult?> AuthorizeEditAsync(
        Guid id, CancellationToken ct, bool allowAdmin = false)
    {
        var playlist = await playlists.GetPlaylistAsync(id, ct);
        if (playlist is null)
            return NotFoundProblem("Playlist not found");
        if (playlist.OwnerId != CurrentUserId && !(allowAdmin && IsAdmin))
            return ForbiddenProblem("Not authorized");
        if (playlist.IsSystem)
            return ForbiddenProblem("System playlists cannot be edited");
        return null;
    }
}
