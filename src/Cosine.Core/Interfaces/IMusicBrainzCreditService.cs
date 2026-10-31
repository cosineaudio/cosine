namespace Cosine.Core.Interfaces;

public interface IMusicBrainzCreditService
{
    /// <summary>
    /// Fills in per-track artist credits from MusicBrainz for albums that do not have them yet.
    /// Returns the number of tracks whose credit was written.
    /// <para>
    /// Opt-in: does nothing, and contacts nobody, unless
    /// <see cref="EnabledSettingKey"/> is "true".
    /// </para>
    /// </summary>
    Task<int> EnrichMissingCreditsAsync(CancellationToken ct = default);

    /// <summary>The setting that gates every outbound request this service makes.</summary>
    const string EnabledSettingKey = "musicbrainz.enrich_track_artists";

    /// <summary>
    /// Server root for the MusicBrainz web service. Points at musicbrainz.org by default;
    /// a self-hosted mirror belongs here, and is the only way to lift the 1 req/sec limit.
    /// </summary>
    const string BaseUrlSettingKey = "musicbrainz.base_url";

    /// <summary>
    /// Contact address advertised in the User-Agent. MusicBrainz's policy asks for one and
    /// throttles anonymous clients harder; it is not an API key, because there is no such thing.
    /// </summary>
    const string ContactSettingKey = "musicbrainz.contact";

    /// <summary>Delay between requests, in milliseconds. Lower it only against a mirror.</summary>
    const string RateLimitSettingKey = "musicbrainz.rate_limit_ms";
}
