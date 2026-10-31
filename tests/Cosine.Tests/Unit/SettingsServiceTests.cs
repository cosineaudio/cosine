using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class SettingsServiceTests
{
    /// <summary>
    /// Fetching artist pictures sends the whole artist list to MusicBrainz and Deezer, so a
    /// self-hosted server must not do it until asked. Existing installs keep whatever value
    /// is already stored - see <see cref="SeedDefaults_DoesNotOverwriteAStoredValue"/>.
    /// </summary>
    [Fact]
    public void ArtistImageFetching_IsOptIn()
    {
        using var sp = new TestServiceProvider();

        Assert.Equal("false", sp.Get<SettingsService>().Defaults["artwork.fetch_artist_images"]);
    }

    /// <summary>
    /// Every seeded key is listed by <c>GET /api/admin/settings</c>, rendered as a control in the
    /// admin panel, and (since #55) the only thing <c>PUT</c> will accept - so a key nothing reads
    /// is a control that silently does nothing, which is what `scan.interval`,
    /// `transcoding.default_format` and `transcoding.default_bitrate` were. Adding a key here
    /// means adding the reader named beside it.
    /// </summary>
    [Fact]
    public void EverySeededSetting_HasAReader()
    {
        using var sp = new TestServiceProvider();

        string[] expected =
        [
            "artwork.fetch_artist_images",     // ArtistImageService.FetchMissingImagesAsync
            "library.media_root",              // ScanJob.RunAsync
            "musicbrainz.base_url",            // MusicBrainzCreditService.EnrichMissingCreditsAsync
            "musicbrainz.contact",             // MusicBrainzCreditService.ApplyContact
            "musicbrainz.enrich_track_artists",// MusicBrainzCreditService.EnrichMissingCreditsAsync
            "musicbrainz.rate_limit_ms",       // MusicBrainzCreditService.ReadRateLimitAsync
            "party.timeout_minutes",           // ListenPartyCleanupJob.TimeoutSettingKey
            "playhistory.retention_days",      // PlayHistoryCleanupJob.RetentionSettingKey
            "releaseradar.window_days",        // ReleaseRadarJob.Execute
            "scan.enabled",                    // ScheduledScanJob.Execute
            "scan.schedule",                   // Program.cs scheduling + AdminController validation
            "transcoding.cache_max_bytes"      // TranscodeCachePruneJob.CacheMaxBytesSettingKey
        ];

        Assert.Equal(expected, sp.Get<SettingsService>().Defaults.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SeedDefaults_DoesNotOverwriteAStoredValue()
    {
        using var sp = new TestServiceProvider();
        var settings = sp.Get<SettingsService>();
        await settings.SetAsync("artwork.fetch_artist_images", "true");

        await settings.SeedDefaultsAsync();

        Assert.Equal("true", await settings.GetAsync("artwork.fetch_artist_images"));
    }
}
