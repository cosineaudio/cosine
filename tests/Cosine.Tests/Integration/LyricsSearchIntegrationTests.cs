using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Jobs;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

using AtlTrack = ATL.Track;

namespace Cosine.Tests.Integration;

/// <summary>
/// End-to-end lyric-content search: a scan populates SearchIndex.LyricsVector from
/// .lrc sidecars, and search2/search3 (SearchService.SearchAsync) then match on lyric
/// text - including sidecar-only changes next to unchanged audio, which the scan diff
/// alone would miss. Runs against real FLAC files and a real Postgres (tsvector needs it).
/// </summary>
public sealed class LyricsSearchIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cosine_lyricsearch_test")
        .WithUsername("cosine")
        .WithPassword("cosine_test")
        .Build();

    private ServiceProvider _provider = null!;
    private string _mediaRoot = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _mediaRoot = Path.Combine(Path.GetTempPath(), $"cosine-lyricsearch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mediaRoot);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CosineDbContext>(opts =>
            opts.UseNpgsql(_postgres.GetConnectionString(), o => o.UseVector()));
        services.AddSignalR();
        services.AddScoped<ISearchService, SearchService>();
        services.AddSingleton<ISettingsService, NoopSettingsService>();
        services.AddSingleton(Options.Create(new MediaOptions { RootPath = _mediaRoot }));
        services.AddTransient<ScanJob>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_mediaRoot))
            Directory.Delete(_mediaRoot, recursive: true);
    }

    // Scenario 1 + 5: a lyric-only token matches, aggregated across multiple sidecars.
    [Fact]
    public async Task LyricOnly_Token_Matches_And_Aggregates_All_Sidecars()
    {
        // "zephyrqualia" / "brumecantus" appear only in the lyrics, never in metadata.
        CreateFlac("song.flac", t =>
        {
            t.Title = "Ordinary Title";
            t.Artist = "Plain Artist";
            t.Album = "Plain Album";
        });
        WriteSidecar("song.lrc", "[00:01.00]a line with zephyrqualia in it\n");
        WriteSidecar("song.fr.lrc", "[00:01.00]une ligne avec brumecantus dedans\n");

        await RunScanAsync();

        // Both the English-sidecar token and the French-sidecar token resolve to the
        // same track - the vector aggregates every lyric row for that track.
        await AssertFinds("zephyrqualia", "song.flac");
        await AssertFinds("brumecantus", "song.flac");
    }

    // Scenario 2: a token in one track's title outranks the same token in another's lyrics.
    [Fact]
    public async Task Metadata_Outranks_LyricOnly()
    {
        CreateFlac("titled.flac", t =>
        {
            t.Title = "Moonbeam Overture";
            t.Artist = "Artist A";
            t.Album = "Album A";
        });
        CreateFlac("lyrical.flac", t =>
        {
            t.Title = "Something Else";
            t.Artist = "Artist B";
            t.Album = "Album B";
        });
        WriteSidecar("lyrical.lrc", "[00:01.00]a distant moonbeam falls\n");

        await RunScanAsync();

        // With trackCount 1 only the top-ranked hit survives: the title match must win
        // over the lyric-only match for the shared token "moonbeam".
        using var scope = _provider.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var results = await search.SearchAsync("moonbeam", artistCount: 0, albumCount: 0, trackCount: 1);

        var top = Assert.Single(results.Tracks);
        Assert.Equal("titled.flac", top.FilePath);
    }

    // Scenario 3: a sidecar dropped next to an UNCHANGED audio file re-indexes the track.
    [Fact]
    public async Task SidecarOnly_Change_ReIndexes_Unchanged_Audio()
    {
        CreateFlac("track.flac", t =>
        {
            t.Title = "Unremarkable";
            t.Artist = "Nobody";
            t.Album = "Nowhere";
        });

        // First scan: no sidecar yet.
        await RunScanAsync();
        await AssertNotFound("glimmerwraith");

        // Add a sidecar next to the (byte-for-byte unchanged) audio file.
        WriteSidecar("track.lrc", "[00:01.00]the glimmerwraith sings\n");

        // Second scan: the audio file is unchanged, so it is not in touchedTrackIds -
        // only the Phase-3 lyric-affected union re-indexes it.
        await RunScanAsync();
        await AssertFinds("glimmerwraith", "track.flac");
    }

    // Scenario 4: removing the sidecar clears the lyric match but keeps the base entry.
    [Fact]
    public async Task Removing_Sidecar_Clears_Lyric_Match_But_Keeps_Metadata()
    {
        CreateFlac("track.flac", t =>
        {
            t.Title = "Findable Title";
            t.Artist = "Some Artist";
            t.Album = "Some Album";
        });
        WriteSidecar("track.lrc", "[00:01.00]a fleeting cindervale\n");

        await RunScanAsync();
        await AssertFinds("cindervale", "track.flac");

        File.Delete(Path.Combine(_mediaRoot, "track.lrc"));
        await RunScanAsync();

        // Lyric token no longer matches...
        await AssertNotFound("cindervale");
        // ...but the base metadata entry survives.
        await AssertFinds("findable", "track.flac");
    }

    private async Task AssertFinds(string query, string expectedFilePath)
    {
        using var scope = _provider.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var results = await search.SearchAsync(query);
        Assert.Contains(results.Tracks, t => t.FilePath == expectedFilePath);
    }

    private async Task AssertNotFound(string query)
    {
        using var scope = _provider.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var results = await search.SearchAsync(query);
        Assert.Empty(results.Tracks);
    }

    private void WriteSidecar(string relativePath, string content)
        => File.WriteAllText(Path.Combine(_mediaRoot, relativePath), content);

    private async Task RunScanAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        var scanLog = new Core.Entities.ScanLog { StartedAt = DateTimeOffset.UtcNow, Status = "running" };
        db.ScanLogs.Add(scanLog);
        await db.SaveChangesAsync();

        var job = scope.ServiceProvider.GetRequiredService<ScanJob>();
        await job.RunAsync(scanLog.Id, CancellationToken.None);

        var completed = await db.ScanLogs.AsNoTracking().SingleAsync(sl => sl.Id == scanLog.Id);
        Assert.Equal("completed", completed.Status);
    }

    private string CreateFlac(string relativePath, Action<AtlTrack> tag)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac");
        var target = Path.Combine(_mediaRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(fixturePath, target);

        var track = new AtlTrack(target);
        tag(track);
        Assert.True(track.Save(), $"failed to write tags to {relativePath}");
        return target;
    }

    private sealed class NoopSettingsService : ISettingsService
    {
        public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>();

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SeedDefaultsAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
