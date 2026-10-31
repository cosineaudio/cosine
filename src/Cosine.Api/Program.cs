using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using Quartz;
using Serilog;
using StackExchange.Redis;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Cosine.Infrastructure.Hubs;
using Cosine.Infrastructure.Jobs;
using Cosine.Infrastructure.Playback;
using Cosine.Infrastructure.Telemetry;
using Cosine.Api.HealthChecks;
using OpenTelemetry.Metrics;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp;
using Microsoft.Extensions.Options;
using Cosine.Api.Middleware;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.StaticFiles", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

// Store the Data Protection key ring in Postgres (the persistent store we already run)
// rather than the container's ephemeral $HOME. Without this ASP.NET warns on every boot
// that the keys won't survive container recreate. cosine's auth is a JWT signed with
// Jwt:SigningKey - not Data Protection - so this is really about silencing that warning
// and keeping any DP-protected payloads stable, with no key volume to manage.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<CosineDbContext>()
    .SetApplicationName("cosine");

// Configuration
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.Section));
builder.Services.Configure<TranscodingOptions>(builder.Configuration.GetSection(TranscodingOptions.Section));
builder.Services.Configure<PlaybackOptions>(builder.Configuration.GetSection(PlaybackOptions.Section));
builder.Services.Configure<AudioAnalysisOptions>(builder.Configuration.GetSection(AudioAnalysisOptions.Section));

// Database
builder.Services.AddDbContext<CosineDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("cosine"), o => o.UseVector()));

// In-memory performance counters (cache hits, transcode ratio, stream TTFB).
builder.Services.AddSingleton<IMetricsService, MetricsService>();

// OpenTelemetry metrics for external monitoring (Prometheus/Grafana). CosineMetrics is the
// domain meter; the rest are the built-in .NET meters - HTTP server/client, Kestrel, runtime
// (GC, thread pool), EF Core and the Npgsql connection pool - which only cost anything once
// a listener subscribes, i.e. here.
builder.Services.AddSingleton<CosineMetrics>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(
            CosineMetrics.MeterName,
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "System.Net.Http",
            "System.Runtime",
            "Microsoft.EntityFrameworkCore",
            "Npgsql")
        .AddPrometheusExporter());

// Resolved once after migrations run; see the probe below.
builder.Services.AddSingleton<VectorSupport>();
builder.Services.AddSingleton<IVectorSupport>(sp => sp.GetRequiredService<VectorSupport>());

// Redis
builder.Services.AddStackExchangeRedisCache(opts =>
{
    opts.Configuration = builder.Configuration.GetConnectionString("redis");
});

// Decorate the just-registered IDistributedCache so every read is counted as a hit or
// miss. The wrapper only touches the return value of Get/GetAsync - zero added latency.
var cacheDescriptor = builder.Services.Last(d => d.ServiceType == typeof(IDistributedCache));
builder.Services.Remove(cacheDescriptor);
builder.Services.Add(new ServiceDescriptor(typeof(IDistributedCache), sp =>
{
    var inner = (IDistributedCache)(cacheDescriptor.ImplementationInstance
        ?? cacheDescriptor.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, cacheDescriptor.ImplementationType!));
    return new MetricsDistributedCache(inner, sp.GetRequiredService<IMetricsService>());
}, cacheDescriptor.Lifetime));

var redisConnectionString = builder.Configuration.GetConnectionString("redis");
if (!string.IsNullOrEmpty(redisConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisConnectionString));
}

// Services
// Singleton: the failed-attempt counters must outlive the request that recorded them.
builder.Services.AddSingleton<IAuthThrottle>(sp =>
    new AuthThrottle(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>(),
        metrics: sp.GetRequiredService<CosineMetrics>()));
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IMediaLibraryService, MediaLibraryService>();
builder.Services.AddScoped<IPlayHistoryService, PlayHistoryService>();
builder.Services.AddScoped<IStreamingService, StreamingService>();
builder.Services.AddSingleton<IScanService, ScanService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IPlaylistService, PlaylistService>();
builder.Services.AddScoped<IAudioSimilarityService, AudioSimilarityService>();
builder.Services.AddScoped<ISimilarityService, SimilarityService>();
builder.Services.AddScoped<IStarService, StarService>();
builder.Services.AddScoped<IRatingService, RatingService>();
builder.Services.AddScoped<IBookmarkService, BookmarkService>();
builder.Services.AddScoped<IPlayQueueService, PlayQueueService>();
builder.Services.AddScoped<IInternetRadioService, InternetRadioService>();
builder.Services.AddSingleton<IImageResizer, FfmpegImageResizer>();
builder.Services.AddScoped<IArtworkService, ArtworkService>();
builder.Services.AddScoped<ILyricsService, LyricsService>();
builder.Services.AddSingleton<ITranscodingService, TranscodingService>();
builder.Services.AddSingleton<IArtistImageService, ArtistImageService>();
builder.Services.AddSingleton<IMusicBrainzCreditService, MusicBrainzCreditService>();
builder.Services.AddSingleton<IAudioAnalysisService, AudioAnalysisService>();
builder.Services.AddSingleton<IAudioNormalizationService, AudioNormalizationService>();
// Singleton: now-playing is in-memory live state shared by every request (see NowPlayingService).
builder.Services.AddSingleton<INowPlayingService, NowPlayingService>();

// Listen together. The service is scoped (DbContext); the ticket service and state cache are
// singletons - tickets only hold a protector, and live position is process-wide state shared
// by the hub, the snapshot endpoint and the idle sweep.
builder.Services.AddScoped<IListenPartyService, ListenPartyService>();
builder.Services.AddSingleton<IListenPartyTicketService, ListenPartyTicketService>();
builder.Services.AddSingleton<IListenPartyStateCache, ListenPartyStateCache>();

// Remote playback.
// Singleton: a live session owns a connected device and a poll loop, which no request scope can
// hold - the same reason NowPlayingService is a singleton.
builder.Services.AddSingleton<IPlaybackSessionManager, PlaybackSessionManager>();
// Scoped: it reads track metadata through the scoped CosineDbContext.
builder.Services.AddScoped<IPlaybackStreamResolver, PlaybackStreamResolver>();
// A continuous poll loop and long-lived multicast listeners, not work on a schedule, so this is
// a hosted service rather than a Quartz job like the rest of this file's background work.
builder.Services.AddHostedService<PlaybackEngine>();

// Drivers are registered here and nowhere else: this is the only place in the codebase that
// knows the driver projects exist (plan §4.6). The Loopback driver is deliberately absent -
// it is a test double in a test-only project, registered by the test host.
builder.Services.Configure<UpnpOptions>(builder.Configuration.GetSection(UpnpOptions.Section));
// Singleton: it owns the multicast sockets and one HttpClient pool shared across every renderer,
// and its discovery loop runs for the life of the process.
builder.Services.AddSingleton<IPlaybackDriver>(sp => new UpnpDriver(
    sp.GetRequiredService<IOptions<UpnpOptions>>().Value,
    profiles: null,
    http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("upnp"),
    loggerFactory: sp.GetRequiredService<ILoggerFactory>()));

// Redis is registered conditionally (see above), so resolve it optionally here.
builder.Services.AddScoped<IStatsService>(sp => new StatsService(
    sp.GetRequiredService<CosineDbContext>(),
    sp.GetRequiredService<IMetricsService>(),
    sp.GetRequiredService<ITranscodingService>(),
    sp.GetService<IConnectionMultiplexer>()));

// MusicBrainz requires an identifying User-Agent; Deezer and image CDNs get it too.
builder.Services.AddHttpClient("artist-images", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("cosine/1.0 (self-hosted music server)");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Its own client because the credit enrichment appends an operator-supplied contact to the
// User-Agent, and mutating the shared one would leak that into the artwork requests too.
builder.Services.AddHttpClient("musicbrainz", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("cosine/1.0 (self-hosted music server)");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// SignalR
// Hub payloads are pinned to camelCase so they match what the controllers emit. Remote playback
// is the first feature to send whole objects over a hub *and* fetch the same shapes over REST
// (PlaybackStateSnapshot arrives both ways), and one property-naming convention per transport
// would mean the browser mirroring a different object depending on which one delivered it.
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// Authentication
var jwtSection = builder.Configuration.GetSection(JwtOptions.Section);
var jwtOptions = jwtSection.Get<JwtOptions>()!;

// Fail fast on an unsafe JWT signing key. HMAC-SHA256 is symmetric, so a placeholder or
// short key lets anyone forge an admin token - refuse to start rather than run insecurely.
ValidateSigningKey(jwtOptions.SigningKey);

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));

builder.Services.AddAuthentication(opts =>
{
    opts.DefaultAuthenticateScheme = "MultiAuth";
    opts.DefaultChallengeScheme = "MultiAuth";
})
// The two API surfaces authenticate differently and exclusively: /rest is OpenSubsonic
// clients only (credentials in the query string or form body - a cookie never authenticates
// there, so a CSRF'd browser request carries nothing /rest will honour), and everything else
// is the web UI's JWT cookie.
.AddPolicyScheme("MultiAuth", "JWT or Subsonic", opts =>
{
    opts.ForwardDefaultSelector = ctx =>
        ctx.Request.Path.StartsWithSegments("/rest", StringComparison.OrdinalIgnoreCase)
            ? SubsonicAuthHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(opts =>
{
    opts.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidAudience = jwtOptions.Audience,
        IssuerSigningKey = signingKey
    };
    opts.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            // Read JWT from httpOnly cookie
            var token = context.Request.Cookies["cosine_auth"];
            if (!string.IsNullOrEmpty(token))
                context.Token = token;
            return Task.CompletedTask;
        },
        // A valid signature is not enough: without this a token issued before a password
        // change (or before the account was deleted) stays usable for its full lifetime.
        OnTokenValidated = async context =>
        {
            var users = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
            var reason = await JwtRevocation.CheckAsync(
                context.Principal!, users, context.HttpContext.RequestAborted);

            if (reason is not null)
            {
                context.Response.Cookies.Delete("cosine_auth");
                context.Fail(reason);
            }
        }
    };
})
.AddScheme<SubsonicAuthOptions, SubsonicAuthHandler>(SubsonicAuthHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("RequireAdmin", policy => policy.RequireClaim("IsAdmin", "True"));
});

// CORS
builder.Services.AddCors(opts =>
{
    opts.AddPolicy("SubsonicApi", p => p
        .AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
        .WithExposedHeaders("Content-Disposition", "Content-Length", "Accept-Ranges"));
});

// Controllers
builder.Services.AddControllers(opts =>
{
    opts.Filters.Add<SubsonicExceptionFilter>();
    opts.Filters.Add<SubsonicEmptyResultFilter>();
    opts.OutputFormatters.Insert(0, new SubsonicOutputFormatter());
});

// Health checks. Registered as singletons so stateful checks (ffmpeg memoizes its probe)
// keep their state between polls - AddCheck<T> alone would activate a fresh instance each run.
builder.Services.AddSingleton<RedisHealthCheck>();
builder.Services.AddSingleton<MediaLibraryHealthCheck>();
builder.Services.AddSingleton<FfmpegHealthCheck>();
builder.Services.AddSingleton<TranscodeCacheDiskHealthCheck>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CosineDbContext>("postgres")
    .AddCheck<RedisHealthCheck>("redis")
    .AddCheck<MediaLibraryHealthCheck>("media-library")
    .AddCheck<FfmpegHealthCheck>("ffmpeg")
    .AddCheck<TranscodeCacheDiskHealthCheck>("transcode-cache-disk");

// Quartz.NET scheduler
builder.Services.AddQuartz(q =>
{
    // Duration + outcome metrics for every job registered below.
    q.AddJobListener<MetricsJobListener>();

    // TranscodeCachePruneJob - every 6 hours
    q.ScheduleJob<TranscodeCachePruneJob>(trigger => trigger
        .WithIdentity("transcode-cache-prune")
        .WithSimpleSchedule(s => s.WithIntervalInHours(6).RepeatForever())
        .StartAt(DateTimeOffset.UtcNow.AddMinutes(1)),
        job => job.WithIdentity("transcode-cache-prune-job"));

    // ScheduledScanJob - daily at 3am
    q.ScheduleJob<ScheduledScanJob>(trigger => trigger
        .WithIdentity("scheduled-scan")
        .WithCronSchedule("0 0 3 * * ?")
        .StartAt(DateTimeOffset.UtcNow.AddMinutes(2)),
        job => job.WithIdentity("scheduled-scan-job"));

    // PlayHistoryCleanupJob - weekly on Sunday at 4am
    q.ScheduleJob<PlayHistoryCleanupJob>(trigger => trigger
        .WithIdentity("play-history-cleanup")
        .WithCronSchedule("0 0 4 ? * SUN")
        .StartAt(DateTimeOffset.UtcNow.AddMinutes(3)),
        job => job.WithIdentity("play-history-cleanup-job"));

    // StatSnapshotJob - hourly, persists interval metrics for dashboard trends
    q.ScheduleJob<StatSnapshotJob>(trigger => trigger
        .WithIdentity("stat-snapshot")
        .WithSimpleSchedule(s => s.WithIntervalInHours(1).RepeatForever())
        .StartAt(DateTimeOffset.UtcNow.AddMinutes(5)),
        job => job.WithIdentity("stat-snapshot-job"));

    // ReleaseRadarJob - every Friday at 00:00 (server local time), rebuilds each
    // user's system "Release Radar" playlist from the rolling new-releases window.
    q.ScheduleJob<ReleaseRadarJob>(trigger => trigger
        .WithIdentity("release-radar")
        .WithCronSchedule("0 0 0 ? * FRI")
        .StartAt(DateTimeOffset.UtcNow.AddMinutes(4)),
        job => job.WithIdentity("release-radar-job"));

    // ListenPartyCleanupJob - every 5 minutes; the party idle timeout is minutes-scale,
    // so a daily sweep would make the configured value meaningless.
    q.ScheduleJob<ListenPartyCleanupJob>(trigger => trigger
        .WithIdentity("listen-party-cleanup")
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever())
        .StartAt(DateTimeOffset.UtcNow.AddMinutes(2)),
        job => job.WithIdentity("listen-party-cleanup-job"));
});

builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

// OpenAPI (dev only)
builder.Services.AddOpenApi();

var app = builder.Build();

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
    await db.Database.MigrateAsync();

    // Audio-similarity features live in a pgvector column. The migration that creates them
    // is guarded, so on a server without pgvector it silently does nothing - this probe is
    // what tells the rest of the app which of the two worlds it is in.
    var vectors = scope.ServiceProvider.GetRequiredService<VectorSupport>();
    await vectors.ProbeAsync(db);
    if (vectors.IsAvailable)
        Log.Information("pgvector available - content-based audio similarity enabled");
    else
        Log.Warning(
            "pgvector not installed - audio similarity disabled, falling back to metadata-only "
            + "scoring. Use the pgvector/pgvector image (or install the extension) to enable it.");

    var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
    await settingsService.SeedDefaultsAsync();

    // Apply the configured scan cron; the trigger registered with AddQuartz only
    // carries the built-in default.
    var scanCron = await settingsService.GetAsync("scan.schedule");
    if (!string.IsNullOrWhiteSpace(scanCron) && CronExpression.IsValidExpression(scanCron))
    {
        var schedulerFactory = scope.ServiceProvider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler();
        var scanTrigger = TriggerBuilder.Create()
            .WithIdentity("scheduled-scan")
            .WithCronSchedule(scanCron)
            .ForJob("scheduled-scan-job")
            .Build();
        if (await scheduler.RescheduleJob(new TriggerKey("scheduled-scan"), scanTrigger) is null)
            Log.Warning("Could not apply scan schedule '{Cron}': trigger not found", scanCron);
        else
            Log.Information("Scan schedule applied: '{Cron}'", scanCron);
    }
    else if (!string.IsNullOrWhiteSpace(scanCron))
    {
        Log.Warning("Ignoring invalid scan.schedule cron expression '{Cron}'", scanCron);
    }

    // Scans run in-process, so none can be running at startup: any "running" scan
    // log is a leftover from a killed/restarted process and would block new scans.
    var interruptedScans = await db.ScanLogs.Where(sl => sl.Status == "running").ToListAsync();
    foreach (var interrupted in interruptedScans)
    {
        interrupted.Status = "failed";
        interrupted.FinishedAt = DateTimeOffset.UtcNow;
        interrupted.ErrorMessage = "Scan interrupted by server restart";
    }
    if (interruptedScans.Count > 0)
    {
        await db.SaveChangesAsync();
        Log.Warning("Marked {Count} interrupted scan(s) as failed at startup", interruptedScans.Count);
    }

    // Seed the scan-freshness gauge from the newest completed scan, so a restart doesn't
    // zero it and trip any "scans have stopped" alert until the next scan runs.
    var lastCompletedScan = await db.ScanLogs
        .Where(sl => sl.Status == "completed" && sl.FinishedAt != null)
        .OrderByDescending(sl => sl.FinishedAt)
        .Select(sl => sl.FinishedAt)
        .FirstOrDefaultAsync();
    if (lastCompletedScan is not null)
        app.Services.GetRequiredService<CosineMetrics>().SetLastSuccessfulScan(lastCompletedScan.Value);

    // Bootstrap admin user if no users exist
    await Cosine.Api.AdminBootstrap.EnsureAdminAsync(
        scope.ServiceProvider.GetRequiredService<IUserService>(),
        scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Cosine.Api.AdminBootstrap"));
}

// Pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// One completed-request line per request, but the high-volume media routes
// (cover art, audio streams, avatars), the SPA's own static assets and infra
// polling (health, SignalR) are demoted below the Information minimum so they're
// suppressed - otherwise a single page load buries every other log line under a
// hundred 304s, and a player session buries it under getCoverArt spam.
app.UseSerilogRequestLogging(opts =>
{
    opts.GetLevel = (ctx, elapsed, ex) =>
    {
        if (ex is not null || ctx.Response.StatusCode >= 500)
            return Serilog.Events.LogEventLevel.Error;

        var path = ctx.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/rest/getCoverArt", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/rest/stream", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/rest/getAvatar", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/stream", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/artwork", StringComparison.OrdinalIgnoreCase)
            // Party guest media: high-volume for the same reason the cookie'd routes are.
            || (path.StartsWith("/api/party/", StringComparison.OrdinalIgnoreCase)
                && (path.Contains("/stream/", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/artwork/", StringComparison.OrdinalIgnoreCase)))
            || path.StartsWith("/playback/coverart", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)
            || IsStaticAsset(path))
            return Serilog.Events.LogEventLevel.Verbose;

        return Serilog.Events.LogEventLevel.Information;
    };
});

// The built SPA: everything under /assets, plus the shell and the odds and ends
// Vite emits beside it. Matched by extension rather than by listing filenames,
// so a new icon or manifest does not quietly start logging again. Nothing the
// API serves has these extensions, so there is no route this can swallow.
static bool IsStaticAsset(string path)
{
    if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)) return true;

    var extension = Path.GetExtension(path);
    return extension.Length > 0 && extension.ToLowerInvariant() is
        ".js" or ".mjs" or ".css" or ".map" or ".html" or ".ico" or ".svg"
        or ".png" or ".jpg" or ".jpeg" or ".webp" or ".avif" or ".gif"
        or ".woff" or ".woff2" or ".ttf" or ".eot" or ".webmanifest" or ".txt";
}

// Subsonic clients traditionally append a ".view" suffix to every endpoint
// (e.g. /rest/getUser.view). The routes are registered without it, so strip the
// suffix before routing to keep those clients working.
app.Use((context, next) =>
{
    var path = context.Request.Path.Value;
    if (path is not null
        && path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".view", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Path = path[..^".view".Length];
    }
    return next();
});

// OpenSubsonic formPost: clients may POST with the parameters form-encoded in the
// body. The REST surface binds from the query string, so fold the form into the
// query and route the request as a GET.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path.StartsWithSegments("/rest", StringComparison.OrdinalIgnoreCase)
        && context.Request.HasFormContentType)
    {
        var form = await context.Request.ReadFormAsync();
        var query = new QueryBuilder();
        foreach (var (key, values) in context.Request.Query)
            foreach (var value in values)
                query.Add(key, value ?? string.Empty);
        foreach (var (key, values) in form)
            foreach (var value in values)
                query.Add(key, value ?? string.Empty);

        context.Request.QueryString = query.ToQueryString();
        context.Request.Method = HttpMethods.Get;
    }
    await next();
});

// Prometheus scrape endpoint. Auth is deliberately optional: the deployment blocks the ops
// paths (/metrics, /health*) at the WAF and lets Prometheus scrape the container directly,
// so by default the endpoint is open. Setting Metrics:Token (Metrics__Token) adds an in-app
// bearer requirement as defense in depth for deployments whose edge does not filter - then
// Prometheus needs `authorization: { credentials: <token> }` in the scrape config. With a
// token set, a non-matching request falls through the predicate into normal routing (the SPA
// fallback), indistinguishable from any other unknown path.
var metricsToken = builder.Configuration["Metrics:Token"];
app.UseOpenTelemetryPrometheusScrapingEndpoint(context =>
    context.Request.Path == "/metrics" && MetricsRequestAuthorized(context, metricsToken));

// Time-to-first-byte metrics for stream routes (runs after the path rewrites above so
// it matches the normalised /rest/stream and /rest/getTranscodeStream paths).
app.UseMiddleware<TtfbMetricsMiddleware>();

app.UseStaticFiles();

// Explicit so routing runs AFTER the ".view" rewrite above - without this,
// WebApplication inserts routing at the front of the pipeline and the rewrite
// happens too late, 404ing every *.view request.
app.UseRouting();

// CSRF gate for the cookie-authenticated surfaces. Path-based, so it only needs to run
// before authentication; /rest and /hubs are exempt inside the middleware.
app.UseMiddleware<CsrfProtectionMiddleware>();

// Named policy, not the default one: no default policy is registered, so a bare UseCors()
// resolves nothing and silently drops the exposed-header list that range-seeking and
// download-naming Subsonic clients need. AllowAnyOrigin cannot carry credentials, so this
// serves the token-authenticated /rest surface; the web UI is same-origin and unaffected.
app.UseCors("SubsonicApi");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// /health/live: is the process serving requests at all - no dependency checks, for container
// restart policies. /health/ready: all dependency checks, for monitoring/alerting. /health
// stays as the full check set for anything already probing it. Responses are the default
// one-word status; details deliberately aren't exposed on an unauthenticated endpoint.
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

// SignalR hubs
app.MapHub<ScanHub>("/hubs/scan");
app.MapHub<ActivityHub>("/hubs/activity");
app.MapHub<PlaybackHub>("/hubs/playback");
app.MapHub<ListenPartyHub>("/hubs/party");

// SPA fallback
app.MapFallbackToFile("index.html");

app.Run();

// Bearer-token check for the metrics scrape endpoint: open when no token is configured
// (edge filtering is the deployment's gate), constant-time compare when one is.
static bool MetricsRequestAuthorized(HttpContext context, string? configuredToken)
{
    if (string.IsNullOrEmpty(configuredToken))
        return true;

    const string prefix = "Bearer ";
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith(prefix, StringComparison.Ordinal))
        return false;

    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(header[prefix.Length..]),
        Encoding.UTF8.GetBytes(configuredToken));
}

static void ValidateSigningKey(string? signingKey)
{
    const int minBytes = 32; // HMAC-SHA256 needs a 256-bit key to be secure.

    string? problem = null;
    if (string.IsNullOrWhiteSpace(signingKey))
        problem = "it is not set";
    else if (signingKey.Contains("change-me", StringComparison.OrdinalIgnoreCase))
        problem = "it is still the shipped placeholder";
    else if (Encoding.UTF8.GetByteCount(signingKey) < minBytes)
        problem = $"it is shorter than {minBytes} bytes";

    if (problem is null)
        return;

    Log.Fatal(
        "Refusing to start: the JWT signing key is insecure ({Problem}). " +
        "Because JWTs are signed with HMAC-SHA256, this key is also the verification key - " +
        "a weak or default value lets anyone forge an admin session. " +
        "Set Jwt__SigningKey (env) or Jwt:SigningKey (config) to a random secret of at least {MinBytes} bytes, " +
        "e.g. `openssl rand -base64 48`.",
        problem, minBytes);
    Log.CloseAndFlush();

    throw new InvalidOperationException(
        $"Insecure JWT signing key: {problem}. Set Jwt__SigningKey to a random secret of at least {minBytes} bytes.");
}

/// <summary>
/// Named so integration tests can boot the real application through
/// <c>WebApplicationFactory&lt;Program&gt;</c>. A top-level-statements program is otherwise an
/// internal generated class with no name to reference.
/// </summary>
public partial class Program;
