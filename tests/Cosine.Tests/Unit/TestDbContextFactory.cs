using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Unit;

public class TestCosineDbContext : CosineDbContext
{
    public TestCosineDbContext(DbContextOptions<CosineDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SearchIndex>(entity =>
        {
            entity.Ignore(e => e.TextVector);
            entity.Ignore(e => e.LyricsVector);
        });

        // TrackAudioFeature needs no treatment here: CosineDbContext already excludes it on any
        // non-Postgres provider, because pgvector columns and an HNSW index cannot be expressed
        // anywhere else. Its maths is covered in Unit/Audio and its storage in the
        // Testcontainers Postgres suite.
    }
}

public static class TestDbContextFactory
{
    public static CosineDbContext Create() => Create(Guid.NewGuid().ToString());

    /// <summary>
    /// A context over a named store, so a test that needs several of them - one per scope, as the
    /// application itself has - can have them see the same rows. A <see cref="DbContext"/> is not
    /// thread-safe, so sharing one instance across scopes is only safe while nothing in the code
    /// under test runs concurrently.
    /// </summary>
    public static CosineDbContext Create(string databaseName)
    {
        var options = new DbContextOptionsBuilder<CosineDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new TestCosineDbContext(options);
    }
}
