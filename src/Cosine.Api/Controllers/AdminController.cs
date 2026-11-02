using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Jobs;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace Cosine.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "RequireAdmin")]
public class AdminController : ControllerBase
{
    private readonly IMediaLibraryService _library;
    private readonly ISettingsService _settings;
    private readonly IScanService _scan;
    private readonly IUserService _users;
    private readonly ExternalIdentityService _identities;
    private readonly IStatsService _stats;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IMediaLibraryService library,
        ISettingsService settings,
        IScanService scan,
        IUserService users,
        ExternalIdentityService identities,
        IStatsService stats,
        ILogger<AdminController> logger)
    {
        _library = library;
        _settings = settings;
        _scan = scan;
        _users = users;
        _identities = identities;
        _stats = stats;
        _logger = logger;
    }

    // The authenticated admin performing an action, for the audit lines below.
    private string ActingAdmin => User.Identity?.Name ?? "unknown";

    [HttpGet("stats")]
    public async Task<ActionResult<LibraryStats>> GetStats(CancellationToken ct)
    {
        var stats = await _library.GetLibraryStatsAsync(ct);
        return Ok(stats);
    }

    [HttpGet("stats/metrics")]
    public async Task<ActionResult<LiveMetricsDto>> GetLiveMetrics(CancellationToken ct)
    {
        var metrics = await _stats.GetLiveMetricsAsync(ct);
        return Ok(metrics);
    }

    [HttpGet("stats/history")]
    public async Task<ActionResult<IReadOnlyList<StatSnapshot>>> GetStatsHistory([FromQuery] int hours = 24, CancellationToken ct = default)
    {
        var history = await _stats.GetSnapshotHistoryAsync(hours, ct);
        return Ok(history);
    }

    /// <summary>Default recap window. `days=0` still opts out and aggregates everything.</summary>
    public const int DefaultRecapDays = 365;

    [HttpGet("stats/recap")]
    public async Task<ActionResult<RecapDto>> GetRecap(
        [FromQuery] int? days = null, [FromQuery] Guid? userId = null, CancellationToken ct = default)
    {
        // The recap is ~9 aggregate queries over PlayHistory. Omitting `days` used to mean no
        // lower bound at all, so the default view of the dashboard scanned the entire table.
        days ??= DefaultRecapDays;
        DateTimeOffset? since = days is int d && d > 0 ? DateTimeOffset.UtcNow.AddDays(-d) : null;
        var recap = await _stats.GetRecapAsync(userId, since, topN: 10, ct);
        return Ok(recap);
    }

    [HttpGet("settings")]
    public async Task<ActionResult<List<Setting>>> GetSettings(CancellationToken ct)
    {
        var result = new List<Setting>();
        foreach (var (key, defaultValue) in _settings.Defaults)
        {
            var value = await _settings.GetAsync(key, ct) ?? defaultValue;
            result.Add(new Setting { Key = key, Value = value });
        }

        return Ok(result);
    }

    [HttpPut("settings/{key}")]
    public async Task<IActionResult> UpdateSetting(
        string key, [FromBody] UpdateSettingRequest request,
        [FromServices] ISchedulerFactory schedulerFactory, CancellationToken ct)
    {
        // The settings table is a fixed set of keys - GetSettings enumerates Defaults, and the
        // admin page renders exactly those. Anything else was previously written anyway: an
        // unread key that only grows the table, or, past the 128-char key column, a 500.
        if (!_settings.Defaults.ContainsKey(key))
            return BadRequest(new { error = $"Unknown setting '{key}'" });

        if (key == "scan.schedule" && !CronExpression.IsValidExpression(request.Value))
            return BadRequest(new { error = "Invalid cron expression" });

        await _settings.SetAsync(key, request.Value, ct);
        _logger.LogInformation("Admin '{Admin}' updated setting '{Key}' = '{Value}'", ActingAdmin, key, request.Value);

        // Apply a new scan cron immediately instead of waiting for a restart.
        if (key == "scan.schedule")
        {
            var scheduler = await schedulerFactory.GetScheduler(ct);
            var trigger = TriggerBuilder.Create()
                .WithIdentity("scheduled-scan")
                .WithCronSchedule(request.Value)
                .ForJob("scheduled-scan-job")
                .Build();
            await scheduler.RescheduleJob(new TriggerKey("scheduled-scan"), trigger, ct);
        }

        return NoContent();
    }

    [HttpPost("scan")]
    public async Task<ActionResult> StartScan(CancellationToken ct)
    {
        var scanLogId = await _scan.StartScanAsync(ct);
        _logger.LogInformation("Admin '{Admin}' started a manual scan: ScanLogId={ScanLogId}", ActingAdmin, scanLogId);
        return Ok(new { scanLogId });
    }

    /// <summary>
    /// Queues a full search-index rebuild. It used to be awaited inline, so a large library
    /// outlived the proxy timeout and the caller's cancellation token could abort the rebuild
    /// halfway; the job also refuses to run twice concurrently.
    /// </summary>
    [HttpPost("reindex")]
    public async Task<ActionResult> RebuildSearchIndex(
        [FromServices] ISchedulerFactory schedulerFactory, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await ReindexJob.TriggerAsync(scheduler, ct);
        _logger.LogInformation("Admin '{Admin}' queued a search index rebuild", ActingAdmin);
        return Accepted();
    }

    [HttpPost("artist-images")]
    public async Task<ActionResult> FetchArtistImages(
        [FromServices] ISchedulerFactory schedulerFactory, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await ArtistImageJob.TriggerAsync(scheduler, ct);
        return Accepted();
    }

    /// <summary>
    /// Queues a MusicBrainz pass for per-track artist credits. Accepted even when the setting is
    /// off - the job logs and exits without contacting anyone, which is a clearer answer than a
    /// 400 for an operator who has just turned it on in another tab.
    /// </summary>
    [HttpPost("track-credits")]
    public async Task<ActionResult> FetchTrackCredits(
        [FromServices] ISchedulerFactory schedulerFactory, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await TrackCreditJob.TriggerAsync(scheduler, ct);
        _logger.LogInformation("Admin '{Admin}' queued a track credit fetch", ActingAdmin);
        return Accepted();
    }

    /// <summary>
    /// How much of the library has a current-version audio analysis backing content-based
    /// similarity, and whether the feature is usable at all on this server.
    /// </summary>
    [HttpGet("audio-analysis")]
    public async Task<ActionResult<AudioAnalysisStatusDto>> GetAudioAnalysisStatus(
        [FromServices] IAudioAnalysisService analysis,
        [FromServices] IAudioNormalizationService normalization,
        [FromServices] IVectorSupport vectors,
        CancellationToken ct)
    {
        var (analyzed, total) = await analysis.GetCoverageAsync(ct);

        return Ok(new AudioAnalysisStatusDto(
            Available: vectors.IsAvailable,
            Analyzed: analyzed,
            Total: total,
            PendingNormalization: await normalization.CountPendingAsync(ct)));
    }

    /// <summary>
    /// Queues an audio analysis pass. Accepted even where pgvector is missing - the job logs and
    /// exits, which tells an operator more than a 400 would.
    /// </summary>
    [HttpPost("audio-analysis")]
    public async Task<ActionResult> StartAudioAnalysis(
        [FromServices] ISchedulerFactory schedulerFactory, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await AudioAnalysisJob.TriggerAsync(scheduler, ct);
        _logger.LogInformation("Admin '{Admin}' queued an audio analysis pass", ActingAdmin);
        return Accepted();
    }

    [HttpGet("scan-logs")]
    public async Task<ActionResult<List<ScanLog>>> GetScanLogs(CancellationToken ct)
    {
        var logs = await _scan.GetScanLogsAsync(50, ct);
        return Ok(logs);
    }

    [HttpGet("users")]
    public async Task<ActionResult<List<UserDto>>> GetUsers(CancellationToken ct)
    {
        var users = await _users.GetUsersAsync(ct);
        var linkedUserIds = await _identities.GetLinkedUserIdsAsync(ct);
        return Ok(users.Select(user => MapUser(user, linkedUserIds.Contains(user.Id))).ToList());
    }

    [HttpPost("users")]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var user = await _users.CreateUserAsync(request.Username, request.Password, request.Email, request.IsAdmin, ct);
        _logger.LogInformation("Admin '{Admin}' created user '{Username}' (id {UserId}, admin={IsAdmin})",
            ActingAdmin, user.Username, user.Id, request.IsAdmin);
        return CreatedAtAction(nameof(GetUsers), new { id = user.Id }, MapUser(user));
    }

    [HttpPut("users/{id:guid}")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _users.GetUserAsync(id, ct);
        if (user is null)
            return NotFound();

        if (user.PasswordHash is null && !string.IsNullOrEmpty(request.Password))
            return Conflict(new { error = "This account uses single sign-on; manage its password at the identity provider" });

        try
        {
            await _users.UpdateUserAsync(id, request.Email, request.IsAdmin, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            await _users.ResetPasswordAsync(id, request.Password, ct);
        }

        _logger.LogInformation("Admin '{Admin}' updated user '{Username}' (id {UserId}, admin={IsAdmin}, passwordChanged={PasswordChanged})",
            ActingAdmin, user.Username, id, request.IsAdmin, !string.IsNullOrEmpty(request.Password));

        return NoContent();
    }

    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
    {
        var user = await _users.GetUserAsync(id, ct);
        if (user is null)
            return NotFound();

        try
        {
            await _users.DeleteUserAsync(id, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        _logger.LogInformation("Admin '{Admin}' deleted user '{Username}' (id {UserId})", ActingAdmin, user.Username, id);
        return NoContent();
    }

    private static UserDto MapUser(User user, bool hasOidcIdentity = false) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Email = user.Email,
        IsAdmin = user.IsAdmin,
        LastLoginAt = user.LastLoginAt,
        HasLocalPassword = user.PasswordHash is not null,
        Source = hasOidcIdentity ? (user.PasswordHash is not null ? "local+oidc" : "oidc") : "local",
        CreatedAt = user.CreatedAt
    };
}

public record UpdateSettingRequest(string Value);

/// <summary>Audio analysis coverage, for the admin panel.</summary>
/// <param name="Available">
/// False where the server has no pgvector. Everything else is then zero and content-based
/// similarity is off - the radio still works, from tags and listening history alone.
/// </param>
/// <param name="Analyzed">Tracks with a current-version feature vector.</param>
/// <param name="Total">Tracks in the library.</param>
/// <param name="PendingNormalization">
/// Analysed tracks not yet z-scored against the library. They are invisible to similarity until
/// the next normalization pass, which the analysis job runs automatically.
/// </param>
public record AudioAnalysisStatusDto(bool Available, int Analyzed, int Total, int PendingNormalization);

public record UserDto
{
    public string Source { get; init; } = "local";
    public bool HasLocalPassword { get; init; }
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string? Email { get; init; }
    public bool IsAdmin { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record CreateUserRequest
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? Email { get; init; }
    public bool IsAdmin { get; init; }
}

public record UpdateUserRequest
{
    public string? Email { get; init; }
    public bool? IsAdmin { get; init; }
    public string? Password { get; init; }
}
