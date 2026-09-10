using System.Security.Cryptography;
using System.Text;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;
using GenAIPlatform.Domain.Documents;

namespace GenAIPlatform.Infrastructure.Evaluations.RetrievalBaseline;

/// <summary>
/// Maps benchmark corpus entries onto the same document and chunk rows the ingestion
/// pipeline writes, so the baseline measures the production retrieval query against
/// production-shaped data. Row timestamps are fixed so the retrieval tie-breakers stay
/// deterministic across repeated runs.
/// </summary>
internal static class RetrievalBaselineRowFactory
{
    public const string ChunkingProfile = "retrieval-baseline";
    public const string ChunkingProfileVersion = "v1";
    private const string ContentType = "text/markdown";
    private const string SourceExtension = ".md";
    private const string StoragePathPrefix = "retrieval-baseline/";

    private static readonly DateTimeOffset RowTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Document CreateDocument(RetrievalBaselineCorpusDocument document)
    {
        var content = string.Concat(document.Chunks.Select(static chunk => chunk.Text));

        return new Document(
            document.DocumentId,
            document.TenantId,
            document.OwnerUserId,
            document.FileName,
            document.Title,
            ContentType,
            SourceExtension,
            $"{StoragePathPrefix}{document.Id}{SourceExtension}",
            Encoding.UTF8.GetByteCount(content),
            Sha256Hex(content),
            document.Version,
            Enum.Parse<DocumentAccessLevel>(document.AccessLevel),
            DocumentIndexingStatus.Indexed,
            RowTimestamp,
            RowTimestamp,
            FailureReason: null);
    }

    public static DocumentChunk CreateChunk(
        RetrievalBaselineCorpusDocument document,
        RetrievalBaselineCorpusChunk chunk)
    {
        return new DocumentChunk(
            chunk.ChunkId,
            document.DocumentId,
            chunk.DocumentVersion,
            chunk.Position,
            chunk.Text,
            Sha256Hex(chunk.Text),
            CountApproximateTokens(chunk.Text),
            ChunkingProfile,
            ChunkingProfileVersion,
            chunk.Embedding,
            document.EmbeddingModel,
            document.EmbeddingProvider,
            chunk.EmbeddingInputTokens,
            RowTimestamp);
    }

    private static string Sha256Hex(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static int CountApproximateTokens(string value)
    {
        return value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }
}
