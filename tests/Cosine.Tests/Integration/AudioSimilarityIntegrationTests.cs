using Cosine.Audio;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Cosine.Tests.Integration;

/// <summary>
/// The pgvector half of audio similarity, against a real Postgres because none of it can be
/// expressed anywhere else: the guarded migration, the <c>vector(307)</c> round trip, the HNSW
/// index, and - the part that actually matters - that ordering by the database's L2 operator
/// agrees with the distances the feature design is built around.
/// </summary>
public sealed class AudioSimilarityIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cosine_audiosim_test")
        .WithUsername("cosine")
        .WithPassword("cosine_test")
        .Build();

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CosineDbContext>(opts =>
            opts.UseNpgsql(_postgres.GetConnectionString(), o => o.UseVector()));

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        await db.Database.MigrateAsync();

        // Mirrors Program.cs: probing also reloads Npgsql's type catalogue, which was read
        // before the migration created the extension and would otherwise not know `vector`.
        await new VectorSupport().ProbeAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Migration_installs_the_extension_and_the_probe_finds_it()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var support = new VectorSupport();
        await support.ProbeAsync(db);

        Assert.True(support.IsAvailable);
    }

    [Fact]
    public async Task The_normalized_column_is_indexed_with_hnsw_over_l2()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var definition = await db.Database
            .SqlQuery<string>($"""
                SELECT indexdef AS "Value" FROM pg_indexes
                WHERE tablename = 'TrackAudioFeatures' AND indexname = 'IX_TrackAudioFeatures_Normalized'
                """)
            .SingleAsync();

        Assert.Contains("hnsw", definition);
        // Cosine distance would renormalize globally and break the facet-weighting identity that
        // the whole single-column design depends on.
        Assert.Contains("vector_l2_ops", definition);
    }

    [Fact]
    public async Task A_feature_vector_round_trips_through_the_vector_column()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var trackId = await SeedTrackAsync(db, "round trip");
        var raw = Ramp(0.5f);

        db.TrackAudioFeatures.Add(new TrackAudioFeature
        {
            TrackId = trackId,
            FeatureVersion = AudioAnalyzer.FeatureVersion,
            AnalyzedAt = DateTimeOffset.UtcNow,
            AnalyzedMs = 120_000,
            Raw = new Vector(raw),
            Bpm = 123.4,
            KeyRoot = 7,
            KeyMode = 1,
            KeyStrength = 0.62,
            LoudnessDb = -14.2,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.TrackAudioFeatures.SingleAsync(f => f.TrackId == trackId);

        Assert.Equal(FeatureLayout.Dimensions, stored.Raw.ToArray().Length);
        Assert.Equal(raw, stored.Raw.ToArray());
        Assert.Null(stored.Normalized);          // not normalized yet
        Assert.Equal(123.4, stored.Bpm, 4);
        Assert.Equal(7, stored.KeyRoot);
    }

    /// <summary>
    /// The query the radio actually runs. Three tracks at known distances from a probe, ordered
    /// by the database's own L2 operator - if this disagrees with the maths, every ranking built
    /// on top of it is wrong.
    /// </summary>
    [Fact]
    public async Task Nearest_neighbour_search_orders_by_l2_distance()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var near = await SeedFeatureAsync(db, "near", Constant(0.10f));
        var middle = await SeedFeatureAsync(db, "middle", Constant(0.50f));
        var far = await SeedFeatureAsync(db, "far", Constant(3.00f));
        db.ChangeTracker.Clear();

        var probe = new Vector(Constant(0.0f));

        var ranked = await db.TrackAudioFeatures
            .Where(f => f.Normalized != null)
            .OrderBy(f => f.Normalized!.L2Distance(probe))
            .Select(f => f.TrackId)
            .ToListAsync();

        Assert.Equal([near, middle, far], ranked);
    }

    [Fact]
    public async Task Distances_returned_by_postgres_match_the_computed_ones()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var trackId = await SeedFeatureAsync(db, "measured", Constant(0.25f));
        db.ChangeTracker.Clear();

        var probe = new Vector(Constant(0.0f));

        var distance = await db.TrackAudioFeatures
            .Where(f => f.TrackId == trackId)
            .Select(f => f.Normalized!.L2Distance(probe))
            .SingleAsync();

        // Every dimension differs by 0.25, so the L2 distance is 0.25 * sqrt(307).
        Assert.Equal(0.25 * Math.Sqrt(FeatureLayout.Dimensions), distance, 3);
    }

    [Fact]
    public async Task Deleting_a_track_removes_its_analysis()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var trackId = await SeedFeatureAsync(db, "doomed", Constant(1.0f));
        db.ChangeTracker.Clear();

        db.Tracks.Remove(await db.Tracks.SingleAsync(t => t.Id == trackId));
        await db.SaveChangesAsync();

        Assert.False(await db.TrackAudioFeatures.AnyAsync(f => f.TrackId == trackId));
    }

    private static float[] Constant(float value)
    {
        var vector = new float[FeatureLayout.Dimensions];
        Array.Fill(vector, value);
        return vector;
    }

    private static float[] Ramp(float step)
    {
        var vector = new float[FeatureLayout.Dimensions];
        for (var i = 0; i < vector.Length; i++) vector[i] = i * step;
        return vector;
    }

    private static async Task<Guid> SeedFeatureAsync(CosineDbContext db, string title, float[] normalized)
    {
        var trackId = await SeedTrackAsync(db, title);

        db.TrackAudioFeatures.Add(new TrackAudioFeature
        {
            TrackId = trackId,
            FeatureVersion = AudioAnalyzer.FeatureVersion,
            AnalyzedAt = DateTimeOffset.UtcNow,
            AnalyzedMs = 120_000,
            Raw = new Vector(normalized),
            Normalized = new Vector(normalized),
        });
        await db.SaveChangesAsync();

        return trackId;
    }

    private static async Task<Guid> SeedTrackAsync(CosineDbContext db, string title)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = $"Artist {title}" };
        var album = new Album { Id = Guid.NewGuid(), Name = $"Album {title}", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = title,
            AlbumId = album.Id,
            ArtistId = artist.Id,
            FilePath = $"{title}.flac",
        };

        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        return track.Id;
    }
}
