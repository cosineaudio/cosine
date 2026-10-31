using System.Data;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Resolves <see cref="IVectorSupport"/> once at startup by asking the server whether the
/// extension is installed, rather than assuming it from the connection string or config.
/// </summary>
public sealed class VectorSupport : IVectorSupport
{
    private readonly ILogger<VectorSupport>? _logger;

    public VectorSupport(ILogger<VectorSupport>? logger = null) => _logger = logger;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Probes for the extension. Must run <b>after</b> migrations, because the audio-similarity
    /// migration is what installs it (guarded, so it stays a no-op where pgvector was never
    /// packaged for the server). Any failure is treated as "unavailable" - a probe that cannot
    /// answer must not be the reason startup fails - but it is logged rather than swallowed,
    /// because a silently-false probe looks exactly like a server without pgvector.
    /// </summary>
    public async Task ProbeAsync(CosineDbContext db, CancellationToken ct = default)
    {
        try
        {
            // The column must be aliased "Value": that is the single-column name EF's SqlQuery
            // projects onto, and without it this throws rather than returning false.
            IsAvailable = await db.Database
                .SqlQuery<bool>(
                    $"""SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') AS "Value" """)
                .SingleAsync(ct);

            if (IsAvailable) await ReloadTypesAsync(db, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not determine whether pgvector is installed; assuming it is not");
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Makes the connection pool aware of the <c>vector</c> type.
    /// <para>
    /// Npgsql loads the server's type catalogue once, when it first connects - which on a fresh
    /// install is <i>before</i> the migration that creates the extension has run. Without this,
    /// every attempt to write a feature vector fails with "cannot resolve 'vector' to a fully
    /// qualified datatype name" until the process is restarted, so a first run would analyse the
    /// whole library and be unable to store any of it.
    /// </para>
    /// </summary>
    private static async Task ReloadTypesAsync(CosineDbContext db, CancellationToken ct)
    {
        if (db.Database.GetDbConnection() is not NpgsqlConnection connection) return;

        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);

        try
        {
            await connection.ReloadTypesAsync(ct);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
    }
}
