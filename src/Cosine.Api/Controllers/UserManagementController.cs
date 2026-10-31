using System.Security.Claims;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class UserManagementController : User_ManagementControllerBase
{
    private readonly IUserService _userService;

    public UserManagementController(IUserService userService)
    {
        _userService = userService;
    }

    public override async Task<ActionResult<SubsonicResponse>> ChangePassword(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        var user = await _userService.GetUserByUsernameAsync(username, cancellationToken);
        if (user is null)
            throw new SubsonicException(SubsonicErrorCode._70, "User not found");

        if (user.Id != currentUserId && !IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var plainPassword = password.StartsWith("enc:")
            ? System.Text.Encoding.UTF8.GetString(Convert.FromHexString(password[4..]))
            : password;

        // The Subsonic `changePassword` call carries no current password - the request is
        // itself authenticated with the caller's credentials, and the branch above has
        // already established self-or-admin.
        await _userService.ResetPasswordAsync(user.Id, plainPassword, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostChangePassword(
        Body2 body, CancellationToken cancellationToken = default)
    {
        return ChangePassword(body.Username, body.Password, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> CreateUser(
        string username, string password, string email,
        bool? ldapAuthenticated = false, bool? adminRole = false, bool? settingsRole = true,
        bool? streamRole = true, bool? jukeboxRole = false, bool? downloadRole = false,
        bool? uploadRole = false, bool? playlistRole = false, bool? coverArtRole = false,
        bool? commentRole = false, bool? podcastRole = false, bool? shareRole = false,
        bool? videoConversionRole = false, IEnumerable<string>? musicFolderId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var plainPassword = password.StartsWith("enc:")
            ? System.Text.Encoding.UTF8.GetString(Convert.FromHexString(password[4..]))
            : password;

        await _userService.CreateUserAsync(username, plainPassword, email, adminRole == true, cancellationToken);
        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostCreateUser(
        Body7 body, CancellationToken cancellationToken = default)
    {
        return CreateUser(
            body.Username, body.Password, body.Email,
            body.LdapAuthenticated, body.AdminRole, body.SettingsRole,
            body.StreamRole, body.JukeboxRole, body.DownloadRole,
            body.UploadRole, body.PlaylistRole, body.CoverArtRole,
            body.CommentRole, body.PodcastRole, body.ShareRole,
            body.VideoConversionRole, body.MusicFolderId, cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> DeleteUser(
        string username, CancellationToken cancellationToken = default)
    {
        if (!IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var user = await _userService.GetUserByUsernameAsync(username, cancellationToken);
        if (user is null)
            throw new SubsonicException(SubsonicErrorCode._70, "User not found");

        try
        {
            await _userService.DeleteUserAsync(user.Id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new SubsonicException(SubsonicErrorCode._0, ex.Message);
        }

        return Ok();
    }

    public override Task<ActionResult<SubsonicResponse>> PostDeleteUser(
        Body13 body, CancellationToken cancellationToken = default)
    {
        return DeleteUser(body.Username, cancellationToken);
    }

    public override async Task<ActionResult<GetUserResponse>> GetUser(
        string username, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        // Some clients omit the username parameter, meaning "the authenticated user".
        if (string.IsNullOrEmpty(username))
            username = User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;

        var user = await _userService.GetUserByUsernameAsync(username, cancellationToken);
        if (user is null)
            throw new SubsonicException(SubsonicErrorCode._70, "User not found");

        if (user.Id != currentUserId && !IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var response = new GetUserResponse
        {
            SubsonicResponse = new GetUserSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                User = new User
                {
                    Username = user.Username,
                    AdminRole = user.IsAdmin,
                    SettingsRole = true,
                    StreamRole = true,
                    DownloadRole = true,
                    // Stated rather than left to default: this server does not implement
                    // sharing at all, so no client should offer a "share" action.
                    ShareRole = false
                }
            }
        };
        return Ok(response);
    }

    public override Task<ActionResult<GetUserResponse>> PostGetUser(
        Body45 body, CancellationToken cancellationToken = default)
    {
        return GetUser(body.Username, cancellationToken);
    }

    public override async Task<ActionResult<GetUsersResponse>> GetUsers(
        CancellationToken cancellationToken = default)
    {
        if (!IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var users = await _userService.GetUsersAsync(cancellationToken);

        var response = new GetUsersResponse
        {
            SubsonicResponse = new GetUsersSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Users = new Users
                {
                    User = users.Select(u => new User
                    {
                        Username = u.Username,
                        AdminRole = u.IsAdmin,
                        SettingsRole = true,
                        StreamRole = true,
                        DownloadRole = true,
                        ShareRole = false
                    }).ToList()
                }
            }
        };
        return Ok(response);
    }

    public override Task<ActionResult<GetUsersResponse>> PostGetUsers(
        object? body = null, CancellationToken cancellationToken = default)
    {
        return GetUsers(cancellationToken);
    }

    public override async Task<ActionResult<SubsonicResponse>> UpdateUser(
        string username, string password,
        string? email = null, bool? ldapAuthenticated = false, bool? adminRole = false,
        bool? settingsRole = true, bool? streamRole = true, bool? jukeboxRole = false,
        bool? downloadRole = false, bool? uploadRole = false, bool? coverArtRole = false,
        bool? commentRole = false, bool? podcastRole = false, bool? shareRole = false,
        bool? videoConversionRole = false, IEnumerable<string>? musicFolderId = null,
        MaxBitRate? maxBitRate = null, CancellationToken cancellationToken = default)
    {
        // `adminRole` is bound with a `false` default, so a client calling
        // `updateUser?username=x&email=y` would otherwise silently strip the admin flag.
        // Only apply it when the request actually carried the parameter.
        return await UpdateUserCoreAsync(
            username, password, email,
            ParameterSupplied("adminRole") ? adminRole : null,
            cancellationToken);
    }

    public override Task<ActionResult<SubsonicResponse>> PostUpdateUser(
        Body62 body, CancellationToken cancellationToken = default)
    {
        // `adminRole` is nullable with no default in the spec (see tools/openapi/specs/…#/updateUser), so an
        // absent JSON key binds to null and an absent form field does too. null therefore means
        // "omitted" on this path exactly as `ParameterSupplied` does on the query path.
        return UpdateUserCoreAsync(
            body.Username, body.Password ?? string.Empty, body.Email,
            body.AdminRole, cancellationToken);
    }

    private async Task<ActionResult<SubsonicResponse>> UpdateUserCoreAsync(
        string username, string password, string? email, bool? adminRole,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            throw new SubsonicException(SubsonicErrorCode._50, "Not authorized");

        var user = await _userService.GetUserByUsernameAsync(username, cancellationToken);
        if (user is null)
            throw new SubsonicException(SubsonicErrorCode._70, "User not found");

        try
        {
            await _userService.UpdateUserAsync(user.Id, email, adminRole, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new SubsonicException(SubsonicErrorCode._0, ex.Message);
        }

        if (!string.IsNullOrEmpty(password))
        {
            var plainPassword = password.StartsWith("enc:")
                ? System.Text.Encoding.UTF8.GetString(Convert.FromHexString(password[4..]))
                : password;
            await _userService.ResetPasswordAsync(user.Id, plainPassword, cancellationToken);
        }

        return Ok();
    }

    /// <summary>
    /// Whether the request explicitly carried <paramref name="name"/>, as opposed to the
    /// generated binding filling in its default.
    /// </summary>
    private bool ParameterSupplied(string name) =>
        Request.Query.ContainsKey(name) ||
        (Request.HasFormContentType && Request.Form.ContainsKey(name));

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return Guid.Parse(claim!.Value);
    }

    private bool IsAdmin() => User.FindFirst("IsAdmin")?.Value == "True";
}
