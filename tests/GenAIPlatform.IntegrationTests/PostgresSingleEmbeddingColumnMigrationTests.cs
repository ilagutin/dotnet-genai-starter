using GenAIPlatform.Infrastructure.Migrations;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

/// <summary>
/// Migration 0007 removes the duplicate <c>embedding_values</c> column, so it may never run on a
/// database that still holds a chunk whose embedding exists only as an array. These tests drive
/// the two outcomes an operator can meet: a fail-closed run that changes nothing and names the
/// repair, and a successful run after the repair.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresSingleEmbeddingColumnMigrationTests(PostgresRepositoryFixture postgres)
{
    private const string MigrationVersion = "0007";
    private static readonly Guid ZeroMagnitudeChunkId =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [DockerAvailableFact]
    public async Task Migrate_WithAnEmbeddingThatNeverBecameAVector_FailsClosedThenSucceedsAfterRepair()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);
            await LegacyV031SeedData.SeedAsync(connectionString);
            await InsertLegacyChunkAsync(
                connectionString,
                ZeroMagnitudeChunkId,
                [0f, 0f]);

            using var host = new SchemaMigrationTestHost(connectionString);
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            // The message carries counts and the repair path, and nothing that identifies a
            // document or quotes chunk text.
            Assert.Equal(SchemaMigrationErrorCodes.ApplyFailed, exception.ErrorCode);
            Assert.Contains($"'{MigrationVersion}'", exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                "1 document chunk(s) across 1 document(s) still have no embedding_vector",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "Re-index those documents to regenerate valid embeddings, or delete those chunks " +
                "explicitly",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains("rolled back", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                ZeroMagnitudeChunkId.ToString(),
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "unvectorized chunk text",
                exception.Message,
                StringComparison.Ordinal);

            // Nothing was applied: the journal stops at the adopted baseline and the column the
            // migration would have dropped is still there with the row still readable.
            var journal = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            Assert.DoesNotContain(MigrationVersion, journal.Select(static entry => entry.Version));
            Assert.Equal(LegacyV031Baseline.AdoptedThroughVersion, journal[^1].Version);
            Assert.True(await SchemaMigrationTestSupport.ColumnExistsAsync(
                connectionString,
                "document_chunks",
                "embedding_values"));
            // Both the offending chunk and the seeded chunk the migration had already backfilled
            // are unvectorized again, which is what proves the whole script rolled back.
            Assert.Equal(2L, await SchemaMigrationTestSupport.CountUnvectorizedChunksAsync(
                connectionString));

            var attempt = Assert.Single(
                await SchemaMigrationTestSupport.ReadAttemptsAsync(connectionString));
            Assert.Equal(MigrationVersion, attempt.Version);
            Assert.Equal("rolled-back", attempt.Outcome);

            await DeleteChunkAsync(connectionString, ZeroMagnitudeChunkId);
            var repaired = await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.Contains(MigrationVersion, repaired.AppliedVersions);
            Assert.Equal(host.Catalog.Head, repaired.JournalHead);
            Assert.False(await SchemaMigrationTestSupport.ColumnExistsAsync(
                connectionString,
                "document_chunks",
                "embedding_values"));
            Assert.Equal(0L, await SchemaMigrationTestSupport.CountUnvectorizedChunksAsync(
                connectionString));

            // The chunk the seed left with an array only was backfilled rather than discarded,
            // and the surviving column now refuses a chunk without an embedding.
            Assert.Equal(
                1L,
                await SchemaMigrationTestSupport.CountRowsAsync(connectionString, "document_chunks"));
            await AssertEmbeddingVectorIsRequiredAsync(connectionString);
        }
        finally
        {
            await PostgresSchemaTestHelper.RebuildSchemaAsync(connectionString);
        }
    }

    /// <summary>
    /// A historical row whose vector disagrees with its array is not silently resolved in favor
    /// of either copy: the migration stops and asks for the same repair.
    /// </summary>
    [DockerAvailableFact]
    public async Task Migrate_WithAVectorThatDisagreesWithItsArray_FailsClosed()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);
            await LegacyV031SeedData.SeedAsync(connectionString);
            await SetChunkVectorAsync(
                connectionString,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "[0.25,0.75]");

            using var host = new SchemaMigrationTestHost(connectionString);
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.ApplyFailed, exception.ErrorCode);
            Assert.Contains(
                "1 document chunk(s) across 1 document(s) are inconsistent historical rows",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "Re-index those documents to regenerate valid embeddings, or delete those chunks " +
                "explicitly",
                exception.Message,
                StringComparison.Ordinal);
            Assert.True(await SchemaMigrationTestSupport.ColumnExistsAsync(
                connectionString,
                "document_chunks",
                "embedding_values"));
            Assert.DoesNotContain(
                MigrationVersion,
                (await SchemaMigrationTestSupport.ReadJournalAsync(connectionString))
                    .Select(static entry => entry.Version));
        }
        finally
        {
            await PostgresSchemaTestHelper.RebuildSchemaAsync(connectionString);
        }
    }

    private static async Task AssertEmbeddingVectorIsRequiredAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.document_chunks (
                id, document_id, document_version, position, text, text_hash,
                approximate_token_count, chunking_profile, chunking_profile_version,
                embedding_model, embedding_provider, embedding_dimensions, embedding_input_tokens,
                created_at_utc)
            VALUES (
                gen_random_uuid(), '11111111-1111-1111-1111-111111111111', 1, 99, 'no vector',
                '9999999999999999999999999999999999999999999999999999999999999999',
                4, 'plain-text', 'v1', 'test-embedding', 'test-provider', 2, 4,
                '2026-01-01T00:06:00Z');
            """, connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.NotNullViolation, exception.SqlState);
    }

    private static async Task InsertLegacyChunkAsync(
        string connectionString,
        Guid chunkId,
        float[] embedding)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.document_chunks (
                id, document_id, document_version, position, text, text_hash,
                approximate_token_count, chunking_profile, chunking_profile_version,
                embedding_model, embedding_provider, embedding_dimensions, embedding_input_tokens,
                embedding_values, created_at_utc)
            VALUES (
                @chunk_id, '11111111-1111-1111-1111-111111111111', 1, 1,
                'unvectorized chunk text',
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                4, 'plain-text', 'v1', 'test-embedding', 'test-provider',
                @embedding_dimensions, 4, @embedding_values, '2026-01-01T00:05:00Z');
            """, connection);
        command.Parameters.AddWithValue("chunk_id", chunkId);
        command.Parameters.AddWithValue("embedding_dimensions", embedding.Length);
        command.Parameters.AddWithValue("embedding_values", embedding);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetChunkVectorAsync(
        string connectionString,
        Guid chunkId,
        string vectorLiteral)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE genai.document_chunks
            SET embedding_vector = @embedding_vector::vector
            WHERE id = @chunk_id;
            """, connection);
        command.Parameters.AddWithValue("chunk_id", chunkId);
        command.Parameters.AddWithValue("embedding_vector", vectorLiteral);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteChunkAsync(string connectionString, Guid chunkId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM genai.document_chunks WHERE id = @chunk_id;",
            connection);
        command.Parameters.AddWithValue("chunk_id", chunkId);
        await command.ExecuteNonQueryAsync();
    }
}
