using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Cosine.Tests.Unit;

public sealed class TestServiceProvider : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public TestServiceProvider()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<CosineDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddScoped<CosineDbContext, TestCosineDbContext>();

        services.AddDistributedMemoryCache();

        services.AddScoped<UserService>();
        services.AddScoped<BookmarkService>();
        services.AddScoped<PlayQueueService>();
        services.AddScoped<InternetRadioService>();
        services.AddScoped<StarService>();
        services.AddScoped<RatingService>();
        // Content-based similarity is unavailable here by construction: the InMemory provider has
        // no pgvector table to query. That is the same state a server without the extension is in,
        // so these tests exercise the metadata-only fallback path.
        services.AddSingleton<IAudioSimilarityService>(StubAudioSimilarityService.Unavailable);
        services.AddScoped<SimilarityService>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<PlayHistoryService>();
        services.AddScoped<MediaLibraryService>();
        services.AddScoped<SearchService>();
        services.AddScoped<LyricsService>();
        services.AddScoped<SettingsService>();
        // Services that resolve their own scope (ArtistImageService) ask for the interface.
        services.AddScoped<Cosine.Core.Interfaces.ISettingsService>(sp => sp.GetRequiredService<SettingsService>());

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>For services that resolve their own scope per call, like <c>ScanService</c>.</summary>
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    public CosineDbContext DbContext => _scope.ServiceProvider.GetRequiredService<CosineDbContext>();

    public IDistributedCache Cache => _scope.ServiceProvider.GetRequiredService<IDistributedCache>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    private sealed class TestCosineDbContext : CosineDbContext
    {
        public TestCosineDbContext(DbContextOptions<CosineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SearchIndex>(entity =>
            {
                entity.Ignore(e => e.TextVector);
                entity.Ignore(e => e.LyricsVector);
            });
        }
    }
}
