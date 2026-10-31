using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cosine.Infrastructure.Data;

/// <summary>
/// Lets `dotnet ef` create the context without booting the full app host
/// (which requires reachable Redis/Postgres). The connection string is only
/// used if a design-time command actually talks to a database.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CosineDbContext>
{
    public CosineDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__cosine")
            ?? "Host=localhost;Database=cosine;Username=cosine;Password=cosine";

        var options = new DbContextOptionsBuilder<CosineDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;

        return new CosineDbContext(options);
    }
}
