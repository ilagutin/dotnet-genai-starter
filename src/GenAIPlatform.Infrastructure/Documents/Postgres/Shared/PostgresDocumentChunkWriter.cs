using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Postgres;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.Shared;

/// <summary>
/// The single place that writes a chunk row, so indexing completion and the retrieval
/// baseline corpus always produce identical column shapes. The pgvector column is the only
/// stored embedding representation: migration 0007 removed the duplicate <c>real[]</c> copy, so
/// no write can leave a chunk's two representations disagreeing with each other.
/// </summary>
internal static class PostgresDocumentChunkWriter
{
    public static async Task InsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DocumentChunk chunk,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.document_chunks (
                id, document_id, document_version, position, text, text_hash,
                approximate_token_count, chunking_profile, chunking_profile_version,
                embedding_model, embedding_provider, embedding_dimensions, embedding_input_tokens,
                embedding_vector, created_at_utc)
            VALUES (
                @id, @document_id, @document_version, @position, @text, @text_hash,
                @approximate_token_count, @chunking_profile, @chunking_profile_version,
                @embedding_model, @embedding_provider, @embedding_dimensions, @embedding_input_tokens,
                @embedding_vector::vector, @created_at_utc);
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "id", chunk.Id);
        PostgresCommandParameters.Add(command, "document_id", chunk.DocumentId);
        PostgresCommandParameters.Add(command, "document_version", chunk.DocumentVersion);
        PostgresCommandParameters.Add(command, "position", chunk.Position);
        PostgresCommandParameters.Add(command, "text", chunk.Text);
        PostgresCommandParameters.Add(command, "text_hash", chunk.TextHash);
        PostgresCommandParameters.Add(command, "approximate_token_count", chunk.ApproximateTokenCount);
        PostgresCommandParameters.Add(command, "chunking_profile", chunk.ChunkingProfile);
        PostgresCommandParameters.Add(command, "chunking_profile_version", chunk.ChunkingProfileVersion);
        PostgresCommandParameters.Add(command, "embedding_model", chunk.EmbeddingModel);
        PostgresCommandParameters.Add(command, "embedding_provider", chunk.EmbeddingProvider);
        PostgresCommandParameters.Add(command, "embedding_dimensions", chunk.EmbeddingDimensions);
        PostgresCommandParameters.Add(command, "embedding_input_tokens", chunk.EmbeddingInputTokens);
        PostgresCommandParameters.Add(command, "embedding_vector", PostgresVectorParameter.From(chunk.Embedding));
        PostgresCommandParameters.Add(command, "created_at_utc", chunk.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
