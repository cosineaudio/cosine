using System.Security.Claims;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class PlaylistsController : PlaylistsControllerBase
{
    private readonly IPlaylistService _playlistService;
    private readonly IUserService _userService;
    private readonly IRatingService _ratings;

    public PlaylistsController(
        IPlaylistService playlistService, IUserService userService, IRatingService ratings)
    {
        _playlistService = playlistService;
        _userService = userService;
        _ratings = ratings;
    }

    public override async Task<ActionResult<CreatePlaylistResponse>> CreatePlaylist(
        string? playlistId = null, string? name = null,
        IEnumerable<string>? songId = null, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        if (playlistId is not null && Guid.TryParse(playlistId, out var existingId))
        {
            // Update existing playlist
            var playlist = await _playlistService.GetPlaylistAsync(existingId, cancellationToken);
            if (playlist is null)
                throw new SubsonicException(SubsonicErrorCode._70, "Playlist not found");
            if (playlist.OwnerId != userId)
                throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");
            if (playlist.IsSystem)
                throw new SubsonicException(SubsonicErrorCode._50, "System playlists cannot be edited");

            if (name is not null)
                await _playlistService.UpdatePlaylistAsync(existingId, name, ct: cancellationToken);

            if (songId is not null)
            {
                var trackIds = songId
                    .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                    .Where(g => g.HasValue)
                    .Select(g => g!.Value)
                    .ToArray();
                if (trackIds.Length > 0)
                    await _playlistService.AddTracksAsync(existingId, trackIds, cancellationToken);
            }

            playlist = await _playlistService.GetPlaylistAsync(existingId, cancellationToken);
            return Ok(CreatePlaylistResponse(playlist!));
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new SubsonicException(SubsonicErrorCode._0, "Playlist name is required");

        var newPlaylist = await _playlistService.CreatePlaylistAsync(name, userId, cancellationToken);

        if (songId is not null)
        {
            var trackIds = songId
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToArray();
            if (trackIds.Length > 0)
                await _playlistService.AddTracksAsync(newPlaylist.Id, trackIds, cancellationToken);
        }

        var result = await _playlistService.GetPlaylistAsync(newPlaylist.Id, cancellationToken);
        return Ok(CreatePlaylistResponse(result!));
    }

    public override Task<ActionResult<CreatePlaylistResponse>> PostCreatePlaylist(
        Body5 body, CancellationToken cancellationToken = default)
    {
        return CreatePlaylist(body.PlaylistId, body.Name, body.SongId, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> DeletePlaylist(
        string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Playlist not found");

        var userId = GetCurrentUserId();
        var playlist = await _playlistService.GetPlaylistAsync(guid, cancellationToken);
        if (playlist is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Playlist not found");
        if (playlist.OwnerId != userId && !IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");
        if (playlist.IsSystem)
            throw new SubsonicException(SubsonicErrorCode._50, "System playlists cannot be deleted");

        await _playlistService.DeletePlaylistAsync(guid, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostDeletePlaylist(
        Body10 body, CancellationToken cancellationToken = default)
    {
        return DeletePlaylist(body.Id, cancellationToken);
    }

    public override async Task<ActionResult<GetPlaylistResponse>> GetPlaylist(
        string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Playlist not found");

        var playlist = await _playlistService.GetPlaylistAsync(guid, cancellationToken);
        if (playlist is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Playlist not found");

        var userId = GetCurrentUserId();
        if (!playlist.IsPublic && playlist.OwnerId != userId && !IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var response = new GetPlaylistResponse
        {
            SubsonicResponse = new GetPlaylistSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Playlist = MapToPlaylistWithSongs(playlist)
            }
        };

        await new RatingOverlay()
            .AddRange(response.SubsonicResponse.Playlist.Entry)
            .ApplyAsync(_ratings, userId, cancellationToken);

        return Ok(response);
    }

    public override Task<ActionResult<GetPlaylistResponse>> PostGetPlaylist(
        Body34 body, CancellationToken cancellationToken = default)
    {
        return GetPlaylist(body.Id, cancellationToken);
    }

    public override async Task<ActionResult<GetPlaylistsResponse>> GetPlaylists(
        string? username = null, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        // The ticket carries the username as ClaimTypes.Name; there is no "username" claim,
        // so looking for one returned null and sent every client that passes its own
        // username (several do) down the admin-only branch. Matched case-insensitively
        // because authentication resolves users that way.
        if (username is not null && !string.Equals(username, CurrentUsername(), StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAdmin())
                throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");
            var targetUser = await _userService.GetUserByUsernameAsync(username, cancellationToken);
            if (targetUser is null)
                throw new SubsonicException(SubsonicErrorCode._70, "User not found");
            userId = targetUser.Id;
        }

        var playlists = await _playlistService.GetPlaylistsAsync(userId, cancellationToken);

        return Ok(new GetPlaylistsResponse
        {
            SubsonicResponse = new GetPlaylistsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Playlists = new Generated.Playlists
                {
                    Playlist = playlists.Select(MapToPlaylist).ToList()
                }
            }
        });
    }

    public override Task<ActionResult<GetPlaylistsResponse>> PostGetPlaylists(
        Body35? body = null, CancellationToken cancellationToken = default)
    {
        return GetPlaylists(body?.Username, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> UpdatePlaylist(
        string playlistId, string? name = null, string? comment = null,
        bool? @public = null, IEnumerable<string>? songIdToAdd = null,
        IEnumerable<int>? songIndexToRemove = null, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(playlistId, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Playlist not found");

        var userId = GetCurrentUserId();
        var playlist = await _playlistService.GetPlaylistAsync(guid, cancellationToken);
        if (playlist is null)
            throw new SubsonicException(SubsonicErrorCode._70, "Playlist not found");
        if (playlist.OwnerId != userId)
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");
        if (playlist.IsSystem)
            throw new SubsonicException(SubsonicErrorCode._50, "System playlists cannot be edited");

        await _playlistService.UpdatePlaylistAsync(guid, name, comment, @public, cancellationToken);

        if (songIdToAdd is not null)
        {
            var trackIds = songIdToAdd
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToArray();
            if (trackIds.Length > 0)
                await _playlistService.AddTracksAsync(guid, trackIds, cancellationToken);
        }

        if (songIndexToRemove is not null)
        {
            var tracks = playlist.PlaylistTracks.OrderBy(pt => pt.Position).ToList();
            foreach (var index in songIndexToRemove.OrderByDescending(i => i))
            {
                if (index >= 0 && index < tracks.Count)
                    await _playlistService.RemoveTrackAsync(guid, tracks[index].TrackId, cancellationToken);
            }
        }

        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostUpdatePlaylist(
        Body61 body, CancellationToken cancellationToken = default)
    {
        return UpdatePlaylist(body.PlaylistId, body.Name, body.Comment,
            body.Public, body.SongIdToAdd, body.SongIndexToRemove, cancellationToken);
    }

    // Mapping helpers
    private static Generated.Playlist MapToPlaylist(Core.Entities.Playlist p)
    {
        var playlist = new Generated.Playlist
        {
            Id = p.Id.ToString(),
            Name = p.Name,
            Comment = p.Comment,
            Owner = p.Owner?.Username,
            Public = p.IsPublic,
            SongCount = p.TrackCount,
            Duration = (int)(p.DurationMs / 1000),
            Created = p.CreatedAt
        };
        // Non-standard flag (OpenSubsonic extension) so clients can render system
        // playlists (Release Radar) distinctly and hide edit/delete affordances.
        if (p.IsSystem)
            playlist.AdditionalProperties["isSystem"] = true;
        return playlist;
    }

    private static PlaylistWithSongs MapToPlaylistWithSongs(Core.Entities.Playlist p)
    {
        var playlist = new PlaylistWithSongs
        {
            Id = p.Id.ToString(),
            Name = p.Name,
            Comment = p.Comment,
            Owner = p.Owner?.Username,
            Public = p.IsPublic,
            SongCount = p.TrackCount,
            Duration = (int)(p.DurationMs / 1000),
            Created = p.CreatedAt,
            Entry = p.PlaylistTracks
                .OrderBy(pt => pt.Position)
                .Select(pt => MapTrackToChild(pt.Track))
                .ToList()
        };
        if (p.IsSystem)
            playlist.AdditionalProperties["isSystem"] = true;
        return playlist;
    }

    // Delegates rather than reimplements: these two copies outlived the consolidation
    // SongMapping's summary describes, so the play queue, bookmarks and playlists were still
    // serving songs from a mapper that knew nothing about artist credits.
    private static Child MapTrackToChild(Core.Entities.Track t) => SongMapping.ToChild(t);

    private static OkObjectResult CreatePlaylistResponse(Core.Entities.Playlist p) =>
        new(new CreatePlaylistResponse
        {
            SubsonicResponse = new CreatePlaylistSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Playlist = MapToPlaylistWithSongs(p)
            }
        });

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.Parse(sub!);
    }

    private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);

    private bool IsAdmin() => User.FindFirst("IsAdmin")?.Value == "True";
}
