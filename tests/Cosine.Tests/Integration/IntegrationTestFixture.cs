using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Cosine.Tests.Integration;

public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly RedisContainer _redis;
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;

    public IntegrationTestFixture()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg17")
            .WithDatabase("cosine_test")
            .WithUsername("cosine")
            .WithPassword("cosine_test")
            .Build();

        _redis = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _redis.StartAsync();

        var services = new ServiceCollection();

        services.AddDbContext<CosineDbContext>(opts =>
            opts.UseNpgsql(_postgres.GetConnectionString(), o => o.UseVector()));

        services.AddStackExchangeRedisCache(opts =>
        {
            opts.Configuration = _redis.GetConnectionString();
        });

        services.AddScoped<UserService>();
        services.AddScoped<BookmarkService>();
        services.AddScoped<PlayQueueService>();
        services.AddScoped<StarService>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<PlayHistoryService>();
        services.AddScoped<MediaLibraryService>();

        _provider = services.BuildServiceProvider();

        // Apply migrations
        using var migrationScope = _provider.CreateScope();
        var db = migrationScope.ServiceProvider.GetRequiredService<CosineDbContext>();
        await db.Database.MigrateAsync();

        _scope = _provider.CreateScope();
    }

    public async Task DisposeAsync()
    {
        _scope?.Dispose();
        if (_provider is not null)
            await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public CosineDbContext DbContext => _scope.ServiceProvider.GetRequiredService<CosineDbContext>();
    public IDistributedCache Cache => _scope.ServiceProvider.GetRequiredService<IDistributedCache>();
    public IServiceProvider Services => _scope.ServiceProvider;
}
