using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Cosine.Infrastructure.Services;

public class SettingsService : ISettingsService
{
    private readonly Data.CosineDbContext _db;
    private readonly IDistributedCache _cache;

    // Every key here is seeded, listed by GET /api/admin/settings, rendered in the admin panel,
    // and (since #55) the only thing PUT will accept - so a key with no reader is a control that
    // silently does nothing. `scan.interval`, `transcoding.default_format` and
    // `transcoding.default_bitrate` were exactly that and are gone: the schedule is
    // `scan.schedule`, and the transcoding defaults come from TranscodingOptions
    // (Transcoding__* env vars). SettingsServiceTests pins the set against the readers.
    private static readonly Dictionary<string, string> DefaultValues = new()
    {
        ["scan.enabled"] = "false",
        ["scan.schedule"] = "0 0 3 * * ?",
        ["playhistory.retention_days"] = "90",
        // Seeded empty so an unset value falls through to TranscodingOptions.MaxCacheBytes
        // (the Transcoding__MaxCacheBytes env var); any number here overrides it.
        ["transcoding.cache_max_bytes"] = "",
        // Rolling window (days) of newly-added releases the Release Radar job scans.
        ["releaseradar.window_days"] = "7",
        ["library.media_root"] = "",
        // Opt-in: enabling it sends the whole artist list to MusicBrainz and Deezer on the
        // next scan, which is not something a self-hosted server should do by default.
        // While disabled, no external service is contacted and artist pictures fall back
        // to the newest album cover in the library.
        ["artwork.fetch_artist_images"] = "false",
        // Opt-in for the same reason: enabling it sends the library's release ids to
        // MusicBrainz. While disabled, track credits are whatever the files' own tags say,
        // which on a Lidarr-managed library is the primary artist and nothing else.
        ["musicbrainz.enrich_track_artists"] = "false",
        // Point this at a self-hosted mirror to lift the rate limit; blank means the default.
        ["musicbrainz.base_url"] = MusicBrainzCreditService.DefaultBaseUrl,
        // MusicBrainz has no API key. What it asks for is an identifying User-Agent, and it
        // throttles anonymous clients harder - so a contact address is the nearest equivalent.
        ["musicbrainz.contact"] = "",
        // 1 req/sec is the anonymous limit. Only lower it against your own mirror.
        ["musicbrainz.rate_limit_ms"] = "1100",
        // How long a listen-together party may sit idle before the sweep ends it. Read by
        // ListenPartyCleanupJob; "idle" is judged from LastActivityAt plus the live state cache.
        ["party.timeout_minutes"] = "120"
    };

    public IReadOnlyDictionary<string, string> Defaults => DefaultValues;

    public SettingsService(Data.CosineDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var cached = await _cache.GetStringAsync($"cosine:settings:{key}", ct);
        if (cached is not null) return cached;

        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null) return null;

        await _cache.SetStringAsync($"cosine:settings:{key}", setting.Value, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(30)
        }, ct);

        return setting.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
        {
            setting = new Core.Entities.Setting { Key = key, Value = value };
            _db.Settings.Add(setting);
        }
        else
        {
            setting.Value = value;
        }

        await _db.SaveChangesAsync(ct);
        await _cache.RemoveAsync($"cosine:settings:{key}", ct);
    }

    public async Task SeedDefaultsAsync(CancellationToken ct = default)
    {
        foreach (var (key, value) in DefaultValues)
        {
            if (!await _db.Settings.AnyAsync(s => s.Key == key, ct))
            {
                _db.Settings.Add(new Core.Entities.Setting { Key = key, Value = value });
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
