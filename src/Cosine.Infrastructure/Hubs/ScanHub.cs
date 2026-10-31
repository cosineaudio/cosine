using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cosine.Infrastructure.Hubs;

[Authorize]
public class ScanHub : Hub
{
    /// <summary>
    /// Scan lifecycle events are admin telemetry (file counts, paths, error messages), so they
    /// go to this group rather than to every connection. Every client still receives the
    /// content-free <c>LibraryChanged</c> broadcast it needs to refresh browse views.
    /// </summary>
    public const string AdminGroup = "admins";

    // Client methods:
    // ScanStarted(Guid scanLogId)                                              - admins
    // ScanProgress(Guid scanLogId, string phase, int current, int total, ...)  - admins
    // ScanCompleted(Guid scanLogId)                                            - admins
    // ScanError(Guid scanLogId, string message)                                - admins
    // AudioAnalysisStarted(int total)                                          - admins
    // AudioAnalysisState(AudioAnalysisProgressUpdate update)                   - admins
    // AudioAnalysisCompleted(int analyzed, int failed)                         - admins
    // LibraryChanged()                                                         - everyone
    //
    // AudioAnalysisState arrives roughly twice a second and covers both halves of a pass -
    // analysis and the normalization that follows it. Only AudioAnalysisJob sends Completed,
    // because only it knows when both are done.

    /// <summary>
    /// Name of the audio-analysis progress event.
    /// <para>
    /// It is <c>AudioAnalysisState</c> and not <c>AudioAnalysisProgress</c> for one reason: the
    /// payload used to be two positional integers and is now a single object. A browser tab that
    /// was open across the deploy still has the old handler, and under the old name it would
    /// bind the whole object to its first parameter and <c>undefined</c> to its second - which
    /// is not a stale reading but a hard TypeError that takes the page down. Renaming turns an
    /// incompatible change into an event the old client simply never hears.
    /// </para>
    /// <para>
    /// The rule this encodes: a hub method's argument shape is a wire contract, and changing it
    /// under the same name breaks every client that has not reloaded. Rename instead.
    /// </para>
    /// </summary>
    public const string AudioAnalysisState = "AudioAnalysisState";

    public override async Task OnConnectedAsync()
    {
        if (IsAdmin(Context.User))
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);

        await base.OnConnectedAsync();
    }

    /// <summary>Mirrors the "RequireAdmin" authorization policy.</summary>
    public static bool IsAdmin(ClaimsPrincipal? user)
        => user?.HasClaim("IsAdmin", "True") == true;
}
