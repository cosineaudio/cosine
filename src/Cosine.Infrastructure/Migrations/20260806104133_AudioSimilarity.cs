using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cosine.Infrastructure.Migrations
{
    /// <summary>
    /// Audio-similarity schema.
    /// <para>
    /// Hand-written rather than left as EF generated it, because every statement has to survive
    /// a PostgreSQL that has never heard of pgvector. cosine's own compose file ships
    /// <c>pgvector/pgvector:pg17</c>, but a self-hoster may point cosine at a server built from
    /// stock packages, and a migration that hard-fails there would take the whole application
    /// down at startup over an optional feature.
    /// </para>
    /// <para>
    /// So: the extension is only created when the server actually has it packaged, and the table
    /// DDL runs through <c>EXECUTE</c> inside a guard. That indirection is the point - a literal
    /// <c>CREATE TABLE ... vector(307)</c> is resolved when the statement is <i>parsed</i>, so it
    /// would fail even inside a branch that never runs. Dynamic SQL is not parsed until executed.
    /// </para>
    /// <para>
    /// Where this no-ops, <c>IVectorSupport.IsAvailable</c> reports false and similarity falls
    /// back to metadata-only scoring. The EF model still contains the entity either way, which is
    /// why nothing may query <c>TrackAudioFeatures</c> without checking that flag first.
    /// </para>
    /// </summary>
    public partial class AudioSimilarity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
                        CREATE EXTENSION IF NOT EXISTS vector;
                    ELSE
                        RAISE NOTICE 'pgvector is not available on this server; skipping the audio similarity schema. Content-based similarity will stay disabled.';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') THEN
                        RETURN;
                    END IF;

                    EXECUTE $ddl$
                        CREATE TABLE IF NOT EXISTS "TrackAudioFeatures" (
                            "TrackId" uuid NOT NULL,
                            "FeatureVersion" integer NOT NULL,
                            "AnalyzedAt" timestamp with time zone NOT NULL,
                            "AnalyzedMs" bigint NOT NULL,
                            "Raw" vector(307) NOT NULL,
                            "Normalized" vector(307),
                            "DistanceMean" double precision,
                            "DistanceStdDev" double precision,
                            "Bpm" double precision NOT NULL,
                            "KeyRoot" integer NOT NULL,
                            "KeyMode" integer NOT NULL,
                            "KeyStrength" double precision NOT NULL,
                            "LoudnessDb" double precision NOT NULL,
                            CONSTRAINT "PK_TrackAudioFeatures" PRIMARY KEY ("TrackId"),
                            CONSTRAINT "FK_TrackAudioFeatures_Tracks_TrackId"
                                FOREIGN KEY ("TrackId") REFERENCES "Tracks" ("Id") ON DELETE CASCADE
                        )
                    $ddl$;

                    EXECUTE $ddl$
                        CREATE INDEX IF NOT EXISTS "IX_TrackAudioFeatures_FeatureVersion"
                            ON "TrackAudioFeatures" ("FeatureVersion")
                    $ddl$;

                    -- HNSW over L2. The facet weighting depends on it: each block is z-scored and
                    -- scaled by sqrt(weight) so that squared L2 over the whole vector equals the
                    -- weighted sum of per-facet squared distances. Cosine distance renormalizes
                    -- globally and would silently destroy that property.
                    EXECUTE $ddl$
                        CREATE INDEX IF NOT EXISTS "IX_TrackAudioFeatures_Normalized"
                            ON "TrackAudioFeatures" USING hnsw ("Normalized" vector_l2_ops)
                    $ddl$;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Safe unguarded: DROP ... IF EXISTS never has to resolve the vector type, and on a
            // server where Up no-opped there is simply nothing to drop. The extension is left
            // installed - another database on the same server may be using it.
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "TrackAudioFeatures";""");
        }
    }
}
